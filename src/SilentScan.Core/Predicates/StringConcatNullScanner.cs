using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second full-archive practitioner sweep" §G: "String concatenation
/// via the + operator silently nulls the entire result when any operand is NULL" - see <see
/// cref="StringConcatNullFinding"/> for the full scope/precision story and oracle evidence.
///
/// Resolves through <see cref="Lineage.FromScopeResolver"/>'s real per-statement scope chain
/// (Phase 1.5 "one binder") rather than a direct-base-table-only shortcut, matching <see
/// cref="FloatEqualityPredicateScanner"/>'s own precedent - a leaf reached through a view/derived
/// table is still left Unknown (declines the whole chain, never guessed at), but a CTE-shadowed
/// reference now resolves against the CTE's real underlying column instead of being declined or
/// mismatched against an unrelated same-named real table. Inspects the SELECT list of every <see
/// cref="QuerySpecification"/> and the SET clause of every UPDATE - the two real-world sites this
/// gotcha actually appears at (building a display string/composite key) - deliberately not every
/// possible scalar-expression position (RETURN, computed columns, etc.), a stated v1 scope limit
/// rather than a silent gap.
/// </summary>
public static class StringConcatNullScanner
{
    private static readonly HashSet<SqlTypeCategory> StringCategories =
    [
        SqlTypeCategory.Char, SqlTypeCategory.VarChar, SqlTypeCategory.NChar, SqlTypeCategory.NVarChar,
        SqlTypeCategory.Text, SqlTypeCategory.NText,
    ];

    public static IReadOnlyList<StringConcatNullFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var visitor = new Visitor(parseResult.SourcePath, catalog);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    /// <summary>One leaf of a flattened <c>+</c> chain, classified with no guessing.</summary>
    private enum LeafKind
    {
        /// <summary>Not resolvable to a confident string-or-guarded shape - blocks the whole chain.</summary>
        Unknown,

        /// <summary>A string literal or a catalog-resolved char-family column - never NULL unless
        /// the leaf's own nullable-column flag is set.</summary>
        String,

        /// <summary>An <c>ISNULL</c>/<c>COALESCE</c> call whose own arguments each recursively
        /// classify as <see cref="String"/> or <see cref="Guarded"/> - can never be NULL.</summary>
        Guarded,
    }

    private readonly record struct Leaf(LeafKind Kind, bool IsNullableColumn, string? TableQualifiedName, string? ColumnName);

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog) : TSqlFragmentVisitor
    {
        private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

        // Real per-statement CTE scope (Phase 1.5 "one binder") - see FloatEqualityPredicateScanner's
        // own identical field for the full rationale.
        private readonly Stack<IReadOnlyDictionary<string, ResolvedRelation>> cteScopeStack = new();

        public List<StringConcatNullFinding> Findings { get; } = [];

        public override void ExplicitVisit(SelectStatement node)
        {
            cteScopeStack.Push(CteResolver.Resolve(node.WithCtesAndXmlNamespaces, catalog, EmptyResolvedViews, sourcePath, ledger: null));
            base.ExplicitVisit(node);
            cteScopeStack.Pop();
        }

        private FromScopeResolver.ResolutionContext ResolutionContext(IReadOnlyDictionary<string, ResolvedRelation> cteRelations) =>
            new(catalog, EmptyResolvedViews, sourcePath, Ledger: null, cteRelations, ProcScope: null);

        private static List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> ScopeChainOf(
            (IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered) resolved) => [resolved];

        public override void ExplicitVisit(QuerySpecification node)
        {
            var cteRelations = cteScopeStack.Count > 0 ? cteScopeStack.Peek() : EmptyResolvedViews;
            var scopeChain = ScopeChainOf(FromScopeResolver.Resolve(node.FromClause, ResolutionContext(cteRelations)));
            foreach (var element in node.SelectElements.OfType<SelectScalarExpression>())
            {
                InspectTopLevel(element.Expression, scopeChain);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            var spec = node.UpdateSpecification;
            var cteRelations = CteResolver.Resolve(node.WithCtesAndXmlNamespaces, catalog, EmptyResolvedViews, sourcePath, ledger: null);
            var scopeChain = ScopeChainOf(FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext(cteRelations)));
            foreach (var setClause in spec.SetClauses.OfType<AssignmentSetClause>())
            {
                if (setClause.NewValue is ScalarExpression newValue)
                {
                    InspectTopLevel(newValue, scopeChain);
                }
            }

            base.ExplicitVisit(node);
        }

        /// <summary>
        /// Finds every OUTERMOST <c>+</c>-chain root reachable from <paramref name="root"/> WITHOUT
        /// descending into a nested <see cref="QuerySpecification"/> - a subquery has its own FROM
        /// scope, reached separately (and correctly re-scoped) when the outer visitor's own
        /// <see cref="ExplicitVisit(QuerySpecification)"/> traversal gets there, matching <see
        /// cref="FloatEqualityPredicateScanner"/>'s own EqualityCollector precedent.
        /// </summary>
        private void InspectTopLevel(
            ScalarExpression root,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            var collector = new ConcatChainRootCollector();
            root.Accept(collector);
            foreach (var chainRoot in collector.Roots)
            {
                InspectChain(chainRoot, scopeChain);
            }
        }

        private void InspectChain(
            BinaryExpression chainRoot,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            var leaves = new List<Leaf>();
            FlattenAddChain(chainRoot, scopeChain, leaves);

            if (leaves.Any(l => l.Kind == LeafKind.Unknown))
            {
                // Precision-first: any leaf this pass can't confidently classify makes the whole
                // chain's string-vs-arithmetic nature unprovable - decline rather than guess.
                return;
            }

            if (!leaves.Any(l => l.Kind == LeafKind.String))
            {
                // No confirmed string leaf at all - nothing here proves this + is concatenation
                // rather than arithmetic (T-SQL precedence: char-family types are the LOWEST
                // precedence, so + is only ever concatenation once at least one side is genuinely
                // string-typed).
                return;
            }

            var nullableLeaf = leaves.FirstOrDefault(l => l.Kind == LeafKind.String && l.IsNullableColumn);
            if (nullableLeaf.TableQualifiedName is null)
            {
                return;
            }

            Findings.Add(new StringConcatNullFinding(
                nullableLeaf.TableQualifiedName, nullableLeaf.ColumnName!, sourcePath, chainRoot.StartLine, chainRoot.StartColumn));
        }

        /// <summary>
        /// Recursively flattens a <c>+</c> chain (through parenthesis wrapping) into its leaves,
        /// classifying each one. A leaf that is itself a nested <c>+</c> BinaryExpression is
        /// flattened further rather than classified directly.
        /// </summary>
        private void FlattenAddChain(
            ScalarExpression expression,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
            List<Leaf> sink)
        {
            var unwrapped = Unwrap(expression);
            if (unwrapped is BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add } add)
            {
                FlattenAddChain(add.FirstExpression, scopeChain, sink);
                FlattenAddChain(add.SecondExpression, scopeChain, sink);
                return;
            }

            sink.Add(ClassifyLeaf(unwrapped, scopeChain));
        }

        private Leaf ClassifyLeaf(
            ScalarExpression expression,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            switch (expression)
            {
                case StringLiteral:
                    return new Leaf(LeafKind.String, IsNullableColumn: false, TableQualifiedName: null, ColumnName: null);

                case ColumnReferenceExpression columnRef:
                    var resolved = BaseColumnResolver.ResolveBaseColumn(columnRef, sourcePath, scopeChain);
                    if (resolved is not { } r || r.Type is not { } columnType || !StringCategories.Contains(columnType.Category))
                    {
                        // Unresolvable, or resolved to a non-string catalog type - either way this
                        // is not a confirmed string leaf, and a confirmed non-string leaf means the
                        // whole + is arithmetic, not concatenation (T-SQL data type precedence).
                        // Neither case is guessed at; the whole chain declines.
                        return new Leaf(LeafKind.Unknown, false, null, null);
                    }

                    // ColumnProvenance.BaseColumn carries no nullability flag - that is catalog-only
                    // information (Lineage's own provenance model deliberately stays independent of
                    // it, since a view/CTE column has no catalog row at all), so a second, explicit
                    // lookup back into the catalog is the correct place for it, not a gap.
                    var isNullable = catalog.Find(r.TableQualifiedName)?.FindColumn(r.ColumnName)?.IsNullable ?? false;
                    return new Leaf(LeafKind.String, isNullable, r.TableQualifiedName, r.ColumnName);

                case FunctionCall { FunctionName.Value: var name } call
                    when string.Equals(name, "ISNULL", StringComparison.OrdinalIgnoreCase):
                    foreach (var arg in call.Parameters)
                    {
                        var argLeaf = ClassifyLeaf(Unwrap(arg), scopeChain);
                        if (argLeaf.Kind == LeafKind.Unknown)
                        {
                            return new Leaf(LeafKind.Unknown, false, null, null);
                        }
                    }

                    return new Leaf(LeafKind.Guarded, false, null, null);

                // COALESCE parses to its own dedicated ScriptDOM node type (CoalesceExpression,
                // Expressions list), never a generic FunctionCall the way ISNULL does - confirmed
                // directly (a live repro against a real nested LTRIM(RTRIM(COALESCE(...))) shape
                // pulled from the local test database's own corpus caught this exact distinction
                // being missed on the first pass, before this fix landed).
                case CoalesceExpression coalesce:
                    foreach (var arg in coalesce.Expressions)
                    {
                        var argLeaf = ClassifyLeaf(Unwrap(arg), scopeChain);
                        if (argLeaf.Kind == LeafKind.Unknown)
                        {
                            return new Leaf(LeafKind.Unknown, false, null, null);
                        }
                    }

                    return new Leaf(LeafKind.Guarded, false, null, null);

                default:
                    return new Leaf(LeafKind.Unknown, false, null, null);
            }
        }

        private static ScalarExpression Unwrap(ScalarExpression expression) =>
            expression is ParenthesisExpression parenthesis ? Unwrap(parenthesis.Expression) : expression;

        /// <summary>
        /// Collects every outermost <c>+</c> <see cref="BinaryExpression"/> chain root reachable
        /// from a scalar expression, WITHOUT descending into a nested <see cref="QuerySpecification"/>
        /// (own FROM scope, reached separately) and without descending INTO an already-found chain's
        /// own children (a nested <c>+</c> node inside a chain already being flattened by its own
        /// root is not a second, independent chain).
        /// </summary>
        private sealed class ConcatChainRootCollector : TSqlFragmentVisitor
        {
            public List<BinaryExpression> Roots { get; } = [];

            public override void ExplicitVisit(BinaryExpression node)
            {
                if (node.BinaryExpressionType == BinaryExpressionType.Add)
                {
                    Roots.Add(node);
                    // Deliberately does not descend into this node's own First/SecondExpression -
                    // FlattenAddChain (run later, per root) already walks the whole nested chain
                    // itself. Other, unrelated scalar expressions elsewhere in the same statement
                    // (e.g. a sibling SELECT-list element) are still reached via normal traversal
                    // since only THIS node's own children are skipped.
                    return;
                }

                base.ExplicitVisit(node);
            }

            /// <summary>
            /// An <c>ISNULL</c> call reached directly as a top-level scalar expression (rather than
            /// as an operand INSIDE some other <c>+</c> chain, already handled by <see
            /// cref="ClassifyLeaf"/>) guards its own FIRST ("value") argument against NULL - a
            /// <c>+</c> chain sitting directly there can never propagate NULL out of this call, so
            /// it is never recorded as an independent root. <c>ISNULL</c>'s own SECOND argument (the
            /// replacement) is not guarded by this call and is still inspected normally.
            /// </summary>
            public override void ExplicitVisit(FunctionCall node)
            {
                var name = node.FunctionName.Value;
                if (string.Equals(name, "ISNULL", StringComparison.OrdinalIgnoreCase))
                {
                    if (node.Parameters.Count > 1)
                    {
                        node.Parameters[1].Accept(this);
                    }

                    return;
                }

                base.ExplicitVisit(node);
            }

            /// <summary>
            /// <c>COALESCE</c> parses to its own dedicated node type (not a <see cref="FunctionCall"/>
            /// the way <c>ISNULL</c> does) - every argument guards against NULL, so a <c>+</c> chain
            /// sitting directly at any argument position is never recorded as an independent root,
            /// matching <see cref="ClassifyLeaf"/>'s own per-operand handling of the identical shape.
            /// </summary>
            public override void ExplicitVisit(CoalesceExpression node)
            {
                // Deliberately does not call base.ExplicitVisit(node) or Accept any argument - every
                // argument is a guarded position.
            }

            public override void ExplicitVisit(QuerySpecification node)
            {
                // Deliberately does not call base.ExplicitVisit(node) - see this class's own doc
                // comment.
            }
        }
    }
}
