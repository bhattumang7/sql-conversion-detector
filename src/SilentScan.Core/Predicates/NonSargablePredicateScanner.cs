using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Pass 3 Tier-1: syntactic non-sargable predicate detection that needs no type/lineage
/// information (CLAUDE.md: "Tier-1 syntactic rules (no types needed)"). Scoped to comparison
/// and LIKE predicates specifically inside a genuine filter context - WHERE, a JOIN's ON
/// clause, or HAVING's own filter - never a SELECT list, ORDER BY, or GROUP BY
/// (docs/audit-remediation-plan.md Phase 3.1: a function/arithmetic wrap on a column that's
/// never used to filter rows isn't a sargability concern at all, since there's no seek to lose).
///
/// A Tier-1 pattern is purely syntactic by construction - it never needed types to FIRE - but
/// whether it's worth a reader's attention still depends on whether the column is even indexed:
/// `UPPER(Notes) = 'X'` on an unindexed nvarchar(max) costs nothing extra beyond the wrap
/// itself, there was no seek to lose. <see cref="SargabilityFinding.Indexed"/> resolves the
/// column through the same catalog/lineage machinery Pass 3/4 uses for typed findings
/// (<see cref="FromScopeResolver"/>, <see cref="ScalarExpressionResolver"/>), so a consumer can
/// tell "this predicate is on a real, indexed column" from "this predicate is on a column we
/// have no evidence is indexed" - the largest source of unranked noise this pass had before.
/// </summary>
public static class NonSargablePredicateScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    /// <summary>
    /// Real-world aggregate functions never lose "sargability" the way a scalar function wrap does -
    /// COUNT/SUM/AVG/etc. wrapping a column in a HAVING clause (the only place they can appear
    /// alongside a column reference) reflects per-group aggregation, not an avoidable index-
    /// defeating transform (docs/audit-remediation-plan.md Phase 3.1).
    /// </summary>
    private static readonly HashSet<string> AggregateFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "COUNT", "COUNT_BIG", "AVG", "MIN", "MAX",
        "STDEV", "STDEVP", "VAR", "VARP",
        "GROUPING", "GROUPING_ID", "STRING_AGG", "CHECKSUM_AGG", "APPROX_COUNT_DISTINCT",
    };

    /// <summary>Scans with no catalog/lineage context available - every finding's <see cref="SargabilityFinding.Indexed"/> stays null (unresolved), never guessed. Used by callers that only need the syntactic pattern itself (Tier-1's own fixture tests) or that run before a catalog exists.</summary>
    public static IReadOnlyList<SargabilityFinding> Scan(SqlParseResult parseResult) =>
        Scan(parseResult, new DatabaseCatalog(), new LineageCatalog(new Dictionary<string, ResolvedRelation>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), new SkipLedger()));

    /// <summary>
    /// <paramref name="enclosingScope"/> seeds the proc/function/trigger scope a reparsed
    /// dynamic SQL fragment is considered inside, so a #temp table declared in the surrounding
    /// STATIC body resolves inside the dynamic text too - the reparsed fragment has no
    /// CREATE PROCEDURE wrapper of its own to discover the scope from. Null (the default) for
    /// an ordinary top-level scan.
    /// </summary>
    public static IReadOnlyList<SargabilityFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog, LineageCatalog lineage, DynamicSqlScope? enclosingScope = null)
    {
        var visitor = new Visitor(parseResult.SourcePath, catalog, lineage.AllRelations, enclosingScope);
        visitor.SeedEnclosingScope();
        parseResult.Fragment.Accept(visitor);
        return visitor.Findings;
    }

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog, IReadOnlyDictionary<string, ResolvedRelation> resolvedViews, DynamicSqlScope? enclosingScope = null) : TSqlFragmentVisitor
    {
        private readonly Stack<(Dictionary<string, ScopeEntry> ByAlias, List<ScopeEntry> Ordered)> _scopeStack = new();
        private readonly Stack<IReadOnlyDictionary<string, ResolvedRelation>> _cteStack = new();
        private string? _currentProcScope = enclosingScope?.ProcScope;
        private bool _inFilterContext;

        public List<SargabilityFinding> Findings { get; } = [];

        /// <summary>Mirrors TypedPredicateExtractor's identical seed - pushes the enclosing trigger's inserted/deleted pseudo-tables onto the CTE stack before the visitor starts walking, so a reparsed dynamic SQL fragment found inside a trigger body sees them too.</summary>
        public void SeedEnclosingScope()
        {
            if (enclosingScope?.TriggerTarget is { } target)
            {
                _cteStack.Push(BuildTriggerPseudoTableRelations(target));
            }
        }

        /// <summary>
        /// Resets filter context to false for every part of a query specification except its
        /// own WHERE/HAVING (whose own overrides turn it back on) - without this, a WHERE
        /// clause's own nested subquery (an EXISTS/IN (SELECT ...)) would inherit "filter
        /// context = true" for that subquery's unrelated SELECT list, and a top-level SELECT
        /// list would inherit whatever the enclosing scope happened to be.
        /// </summary>
        public override void ExplicitVisit(QuerySpecification node)
        {
            _scopeStack.Push(FromScopeResolver.Resolve(node.FromClause, catalog, resolvedViews, sourcePath, ledger: null, CurrentCteRelations(), _currentProcScope));

            var previous = _inFilterContext;
            _inFilterContext = false;

            node.FromClause?.Accept(this);

            foreach (var element in node.SelectElements)
            {
                element.Accept(this);
            }

            node.WhereClause?.Accept(this);
            node.GroupByClause?.Accept(this);
            node.HavingClause?.Accept(this);
            node.OrderByClause?.Accept(this);
            node.WindowClause?.Accept(this);

            _inFilterContext = previous;
            _scopeStack.Pop();
        }

        public override void ExplicitVisit(SelectStatement node)
        {
            PushCteScope(node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
            _cteStack.Pop();
        }

        /// <summary>UPDATE/DELETE/MERGE predicates get the same FROM-scope resolution SELECT gets (docs/audit-remediation-plan.md Phase 4.1's coverage gap, mirrored here for index resolution).</summary>
        public override void ExplicitVisit(UpdateStatement node)
        {
            var spec = node.UpdateSpecification;
            PushCteScope(node.WithCtesAndXmlNamespaces);
            _scopeStack.Push(FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, CurrentResolutionContext()));
            base.ExplicitVisit(node);
            _scopeStack.Pop();
            _cteStack.Pop();
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            var spec = node.DeleteSpecification;
            PushCteScope(node.WithCtesAndXmlNamespaces);
            _scopeStack.Push(FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, CurrentResolutionContext()));
            base.ExplicitVisit(node);
            _scopeStack.Pop();
            _cteStack.Pop();
        }

        public override void ExplicitVisit(MergeStatement node)
        {
            var spec = node.MergeSpecification;
            PushCteScope(node.WithCtesAndXmlNamespaces);
            _scopeStack.Push(FromScopeResolver.ResolveForMerge(spec.Target, spec.TableAlias, spec.TableReference, CurrentResolutionContext()));

            var previousFilterContext = _inFilterContext;
            _inFilterContext = true;
            base.ExplicitVisit(node);
            _inFilterContext = previousFilterContext;

            _scopeStack.Pop();
            _cteStack.Pop();
        }

        // SelectStatement/UpdateStatement/DeleteStatement/MergeStatement above all push CTE
        // scope; INSERT had no override at all here (unlike TypedPredicateExtractor's identical
        // ExplicitVisit(InsertStatement)), so `WITH cte AS (...) INSERT INTO t SELECT ... FROM
        // cte WHERE UPPER(cte.Col) = 'x'` failed to resolve the `cte` alias in FromScopeResolver -
        // the syntactic FunctionWrappedColumn finding still fired (InspectSide/FindAnyColumn work
        // on the raw AST regardless of scope resolution), but Indexed silently resolved to
        // false/unresolved instead of true, understating the finding for ranking purposes. No
        // FROM scope is pushed for the INSERT target itself (mirrors TypedPredicateExtractor: an
        // INSERT target is never referenced by a predicate), only CTEs, so a CTE referenced by
        // the INSERT ... SELECT source still resolves.
        public override void ExplicitVisit(InsertStatement node)
        {
            PushCteScope(node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
            _cteStack.Pop();
        }

        // Mirrors TypedPredicateExtractor's identical overrides (ScriptDOM's visitor binds
        // ExplicitVisit at compile time to the most specific node type, so a base-type-only
        // override never fires for e.g. AlterProcedureStatement) - needed here so a temp
        // table/table variable declared inside a procedure body resolves through the same
        // scoped catalog key TypedPredicateExtractor and CatalogBuilder use.
        public override void ExplicitVisit(CreateProcedureStatement node) => VisitProcedureOrFunctionBody(node, node.ProcedureReference.Name);

        public override void ExplicitVisit(AlterProcedureStatement node) => VisitProcedureOrFunctionBody(node, node.ProcedureReference.Name);

        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) => VisitProcedureOrFunctionBody(node, node.ProcedureReference.Name);

        public override void ExplicitVisit(CreateFunctionStatement node) => VisitProcedureOrFunctionBody(node, node.Name);

        public override void ExplicitVisit(AlterFunctionStatement node) => VisitProcedureOrFunctionBody(node, node.Name);

        public override void ExplicitVisit(CreateOrAlterFunctionStatement node) => VisitProcedureOrFunctionBody(node, node.Name);

        public override void ExplicitVisit(CreateTriggerStatement node) => VisitTriggerBody(node, node.Name, node.TriggerObject);

        public override void ExplicitVisit(AlterTriggerStatement node) => VisitTriggerBody(node, node.Name, node.TriggerObject);

        public override void ExplicitVisit(CreateOrAlterTriggerStatement node) => VisitTriggerBody(node, node.Name, node.TriggerObject);

        public override void ExplicitVisit(WhereClause node)
        {
            var previous = _inFilterContext;
            _inFilterContext = true;
            node.AcceptChildren(this);
            _inFilterContext = previous;
        }

        public override void ExplicitVisit(HavingClause node)
        {
            var previous = _inFilterContext;
            _inFilterContext = true;
            node.AcceptChildren(this);
            _inFilterContext = previous;
        }

        /// <summary>
        /// A JOIN's ON clause is a filter context exactly like WHERE; the table references it
        /// joins are not (a derived-table subquery there has its own SELECT list to protect).
        /// </summary>
        public override void ExplicitVisit(QualifiedJoin node)
        {
            node.FirstTableReference?.Accept(this);
            node.SecondTableReference?.Accept(this);

            var previous = _inFilterContext;
            _inFilterContext = true;
            node.SearchCondition?.Accept(this);
            _inFilterContext = previous;
        }

        private void VisitProcedureOrFunctionBody(ProcedureStatementBodyBase node, SchemaObjectName name)
        {
            var previousScope = _currentProcScope;
            _currentProcScope = SchemaObjectNameHelper.Qualify(name);
            node.AcceptChildren(this);
            _currentProcScope = previousScope;
        }

        /// <summary>Mirrors TypedPredicateExtractor's identical override - without it, a #temp table declared in a trigger body resolved under no scope key at all in Tier-1, and inserted/deleted were never visible here regardless.</summary>
        private void VisitTriggerBody(TriggerStatementBody node, SchemaObjectName name, TriggerObject triggerObject)
        {
            var previousScope = _currentProcScope;
            _currentProcScope = SchemaObjectNameHelper.Qualify(name);

            // A DDL/LOGON trigger has no target object and no inserted/deleted rowset (it gets
            // its data from EVENTDATA()) - nothing to seed, but still walk the body, since it may
            // still contain ordinary predicates against real tables.
            if (triggerObject.Name is not { } targetTableName)
            {
                node.AcceptChildren(this);
                _currentProcScope = previousScope;
                return;
            }

            _cteStack.Push(MergeCtes(CurrentCteRelations(), BuildTriggerPseudoTableRelations(targetTableName)));
            node.AcceptChildren(this);
            _cteStack.Pop();

            _currentProcScope = previousScope;
        }

        /// <summary>Mirrors TypedPredicateExtractor's identical helper - inserted/deleted are shaped like the trigger's own target table or view, but never claim its index (they're a version-store rowset with none of their own).</summary>
        private IReadOnlyDictionary<string, ResolvedRelation> BuildTriggerPseudoTableRelations(SchemaObjectName targetTableName)
        {
            var qualifiedName = SchemaObjectNameHelper.Qualify(targetTableName);

            ResolvedRelation relation;
            if (resolvedViews.TryGetValue(qualifiedName, out var viewRelation))
            {
                relation = FromScopeResolver.ToPseudoTableRelation(viewRelation, qualifiedName);
            }
            else if (catalog.Find(qualifiedName) is { } table)
            {
                relation = FromScopeResolver.ToPseudoTableRelation(table, qualifiedName);
            }
            else
            {
                return EmptyResolvedViews;
            }

            return new Dictionary<string, ResolvedRelation>(StringComparer.OrdinalIgnoreCase)
            {
                ["inserted"] = relation,
                ["deleted"] = relation,
            };
        }

        public override void Visit(BooleanComparisonExpression node)
        {
            if (!_inFilterContext)
            {
                return;
            }

            InspectSide(node.FirstExpression);
            InspectSide(node.SecondExpression);
        }

        public override void Visit(BooleanTernaryExpression node)
        {
            if (!_inFilterContext)
            {
                return;
            }

            // BETWEEN: "col BETWEEN a AND b" - the tested value is FirstExpression, but a
            // wrapped column can just as easily appear as a range BOUND ("'m' BETWEEN
            // LOWER(LowCode) AND HighCode") - all three positions get the same inspection.
            if (node.TernaryExpressionType == BooleanTernaryExpressionType.Between
                || node.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween)
            {
                InspectSide(node.FirstExpression);
                InspectSide(node.SecondExpression);
                InspectSide(node.ThirdExpression);
            }
        }

        /// <summary>UPPER(col) IN (...) defeats the index exactly like UPPER(col) = '...' - the tested Expression side gets the identical dispatch a comparison's own operand gets. The Values list (and any Subquery) are not inspected: a wrapped literal/subquery element isn't a sargability concern the way the tested expression is.</summary>
        public override void Visit(InPredicate node)
        {
            if (!_inFilterContext)
            {
                return;
            }

            InspectSide(node.Expression);
        }

        public override void Visit(LikePredicate node)
        {
            if (!_inFilterContext)
            {
                return;
            }

            if (node.FirstExpression is not ColumnReferenceExpression columnRef || ColumnName(columnRef) is not { } columnName)
            {
                return;
            }

            switch (node.SecondExpression)
            {
                case StringLiteral { Value: ['%', ..] } literal:
                    Add(SargabilityFindingKind.LeadingWildcardLike, columnName, literal.Value, node, columnRef);
                    break;
                case StringLiteral:
                    // A literal pattern with no leading wildcard is sargable; nothing to report.
                    break;
                default:
                    // The pattern isn't a literal (a parameter/variable/expression) - we can't
                    // rule out a leading wildcard statically. CLAUDE.md: "LIKE @p marked conditional".
                    Add(SargabilityFindingKind.LikePatternNotLiteral, columnName, detail: null, node, columnRef);
                    break;
            }
        }

        private void InspectSide(ScalarExpression expression)
        {
            // Unwrap defensive parens before dispatching - `(CASE WHEN Col = 'x' THEN 1 END) = 1`
            // is exactly as common in real-world SQL as the unparenthesized form (the Microsoft
            // Q&A repro FUNCTION_WRAPPED_COLUMN_case_when_test_fires.sql cites writes it exactly
            // this way), and every case below (CAST/CONVERT/BinaryExpression/CASE/COALESCE/
            // NULLIF/IIF) would otherwise silently miss it - a ParenthesisExpression node sitting
            // directly on top of the wrap defeats every `case SomeWrapType` match here even
            // though FindAnyColumn already unwraps parens fine one level down.
            while (expression is ParenthesisExpression parenthesized)
            {
                expression = parenthesized.Expression;
            }

            switch (expression)
            {
                case FunctionCall { Parameters.Count: > 0 } functionCall
                    when !AggregateFunctionNames.Contains(functionCall.FunctionName.Value) && FirstNamedColumn(functionCall.Parameters) is { } named:
                    Add(SargabilityFindingKind.FunctionWrappedColumn, named.Name, functionCall.FunctionName.Value, functionCall, named.Ref);
                    break;

                case CastCall castCall when FindAnyColumn(castCall.Parameter) is { } found:
                    Add(SargabilityFindingKind.CastOrConvertOnColumn, found.Name, "CAST", castCall, found.Ref);
                    break;

                case ConvertCall convertCall when FindAnyColumn(convertCall.Parameter) is { } found:
                    Add(SargabilityFindingKind.CastOrConvertOnColumn, found.Name, "CONVERT", convertCall, found.Ref);
                    break;

                case BinaryExpression binary:
                    InspectArithmetic(binary);
                    break;

                // CLAUDE.md's own named hard cases (CASE/COALESCE/NULLIF) plus IIF, the shorthand
                // for a two-branch CASE - none of these are FunctionCall nodes (a distinct
                // ScriptDom node type each), so they were invisible here even though ISNULL(...)
                // (which IS a FunctionCall) was already caught above. A column wrapped in any of
                // them is exactly as non-sargable as a column wrapped in a scalar function: the
                // engine can't seek through a CASE/COALESCE/NULLIF/IIF result any more than it can
                // through UPPER(col). Reused under the existing FunctionWrappedColumn kind rather
                // than inventing one per construct, matching how ISNULL already shares it with
                // every other function-wrapped-column case.
                //
                // Searches BOTH the value positions (THEN/ELSE/argument expressions, via
                // FindAnyColumn) AND, for CASE/IIF, the boolean test itself (a SearchedCaseExpression's
                // WhenExpression / IIfCall's Predicate, via FindAnyColumnInBoolean) - a real,
                // documented repro (Microsoft Q&A, Erland Sommarskog: "CASE expressions are not
                // sargable", `WHERE (CASE WHEN MobileNumber = 'x' THEN CAST(1 AS BIT) END) = 1`
                // measured as a 199-read index scan vs a 122-read seek for the unwrapped
                // equivalent) wraps the column in exactly the WHEN-test position, not the THEN
                // value - so both positions have to be covered to catch the shape this rule is
                // actually named for.
                case CoalesceExpression or NullIfExpression or IIfCall or SearchedCaseExpression or SimpleCaseExpression
                    when FindAnyColumn(expression) is { } wrapped:
                    Add(SargabilityFindingKind.FunctionWrappedColumn, wrapped.Name, WrapConstructName(expression), expression, wrapped.Ref);
                    break;
            }
        }

        private static string WrapConstructName(ScalarExpression expression) => expression switch
        {
            CoalesceExpression => "COALESCE",
            NullIfExpression => "NULLIF",
            IIfCall => "IIF",
            CaseExpression => "CASE",
            _ => expression.GetType().Name,
        };

        private void InspectArithmetic(BinaryExpression binary)
        {
            var found = FindAnyColumn(binary.FirstExpression) ?? FindAnyColumn(binary.SecondExpression);
            if (found is { } column)
            {
                Add(SargabilityFindingKind.ColumnArithmetic, column.Name, binary.BinaryExpressionType.ToString(), binary, column.Ref);
            }
        }

        /// <summary>
        /// Recursively searches an expression subtree for the first genuine column reference -
        /// shared by CastOrConvertOnColumn and ColumnArithmetic so a wrapped column doesn't have
        /// to be the DIRECT parameter/operand to be caught (e.g. <c>CAST(ISNULL(col, 0) AS
        /// int)</c>). Deliberately does not descend into a subquery (<see cref="ScalarSubquery"/>
        /// isn't a case here at all, since it's not a <see cref="ScalarExpression"/> subtype this
        /// switch matches) - a column inside a nested SELECT belongs to that subquery's own
        /// filter context, not this one.
        /// </summary>
        private static (ColumnReferenceExpression Ref, string Name)? FindAnyColumn(ScalarExpression expression) => expression switch
        {
            ColumnReferenceExpression columnRef when ColumnName(columnRef) is { } name => (columnRef, name),
            ParenthesisExpression parenthesis => FindAnyColumn(parenthesis.Expression),
            UnaryExpression unary => FindAnyColumn(unary.Expression),
            CastCall castCall => FindAnyColumn(castCall.Parameter),
            ConvertCall convertCall => FindAnyColumn(convertCall.Parameter),
            BinaryExpression binary => FindAnyColumn(binary.FirstExpression) ?? FindAnyColumn(binary.SecondExpression),
            FunctionCall functionCall => functionCall.Parameters.Select(FindAnyColumn).FirstOrDefault(r => r is not null),
            CoalesceExpression coalesce => coalesce.Expressions.Select(FindAnyColumn).FirstOrDefault(r => r is not null),
            NullIfExpression nullIf => FindAnyColumn(nullIf.FirstExpression) ?? FindAnyColumn(nullIf.SecondExpression),
            // IIF's own boolean test is searched too, not just its Then/Else values - see the
            // CASE case below for why (same construct, shorthand two-branch form).
            IIfCall iif => FindAnyColumnInBoolean(iif.Predicate) ?? FindAnyColumn(iif.ThenExpression) ?? FindAnyColumn(iif.ElseExpression),
            SimpleCaseExpression simpleCase => FindAnyColumn(simpleCase.InputExpression)
                ?? simpleCase.WhenClauses.Select(w => FindAnyColumn(w.ThenExpression)).FirstOrDefault(r => r is not null)
                ?? (simpleCase.ElseExpression is { } elseExpr ? FindAnyColumn(elseExpr) : null),
            // Searches each WHEN's own boolean test (FindAnyColumnInBoolean) as well as every
            // THEN/ELSE value - a real, documented repro (Microsoft Q&A, Erland Sommarskog)
            // wraps the column in exactly the WHEN-test position (`CASE WHEN MobileNumber = 'x'
            // THEN CAST(1 AS BIT) END = 1`), not the THEN value, so only searching THEN/ELSE
            // would miss the shape this rule is actually named for.
            SearchedCaseExpression searchedCase => searchedCase.WhenClauses.Select(w => FindAnyColumnInBoolean(w.WhenExpression)).FirstOrDefault(r => r is not null)
                ?? searchedCase.WhenClauses.Select(w => FindAnyColumn(w.ThenExpression)).FirstOrDefault(r => r is not null)
                ?? (searchedCase.ElseExpression is { } elseExpr ? FindAnyColumn(elseExpr) : null),
            _ => null,
        };

        /// <summary>
        /// Mirrors <see cref="FindAnyColumn"/> but over the boolean-expression grammar (a CASE
        /// WHEN test, an IIF predicate) rather than the scalar-expression one - a column
        /// referenced only inside the boolean TEST that decides which branch to take is just as
        /// wrapped/non-sargable as one in the THEN/ELSE value (both are inside the same opaque
        /// CASE/IIF the engine can't seek through). Deliberately does not descend into a
        /// subquery-bearing predicate (EXISTS/IN/quantified comparison) - a column inside a
        /// nested SELECT belongs to that subquery's own filter context, not this one, matching
        /// <see cref="FindAnyColumn"/>'s own subquery exclusion.
        /// </summary>
        private static (ColumnReferenceExpression Ref, string Name)? FindAnyColumnInBoolean(BooleanExpression expression) => expression switch
        {
            BooleanComparisonExpression comparison => FindAnyColumn(comparison.FirstExpression) ?? FindAnyColumn(comparison.SecondExpression),
            BooleanBinaryExpression binary => FindAnyColumnInBoolean(binary.FirstExpression) ?? FindAnyColumnInBoolean(binary.SecondExpression),
            BooleanNotExpression not => FindAnyColumnInBoolean(not.Expression),
            BooleanParenthesisExpression parenthesis => FindAnyColumnInBoolean(parenthesis.Expression),
            BooleanIsNullExpression isNull => FindAnyColumn(isNull.Expression),
            _ => null,
        };

        /// <summary>The first parameter that's a genuine named column reference - COUNT(*) etc. have a Wildcard ColumnReferenceExpression with no MultiPartIdentifier, which isn't "a column" for this rule's purposes.</summary>
        private static (ColumnReferenceExpression Ref, string Name)? FirstNamedColumn(IList<ScalarExpression> parameters)
        {
            foreach (var parameter in parameters.OfType<ColumnReferenceExpression>())
            {
                if (ColumnName(parameter) is { } name)
                {
                    return (parameter, name);
                }
            }

            return null;
        }

        private static string? ColumnName(ColumnReferenceExpression columnRef) =>
            columnRef.MultiPartIdentifier?.Identifiers is { Count: > 0 } identifiers ? identifiers[^1].Value : null;

        private void Add(SargabilityFindingKind kind, string columnName, string? detail, TSqlFragment node, ColumnReferenceExpression columnRef)
        {
            var (tableQualifiedName, indexed) = ResolveIndexInfo(columnRef);
            Findings.Add(new SargabilityFinding(
                kind, columnName, detail, sourcePath, node.StartLine, node.StartColumn,
                TableQualifiedName: tableQualifiedName, Indexed: indexed, PredicateFragmentText: Rules.FragmentTextRenderer.Render(node)));
        }

        /// <summary>
        /// Resolves <paramref name="columnRef"/> through the current scope chain to find out
        /// whether it's a real, leading-key-indexed catalog column - the same resolution
        /// TypedPredicateExtractor performs for typed findings, reused here so a syntactic
        /// finding carries the same "is this actually worth a reader's attention" signal. Never
        /// guesses: any provenance other than a direct BaseColumn/Declared passthrough (a
        /// CAST-derived column, a UNION branch, an unresolvable reference) reports
        /// TableQualifiedName=null/Indexed=null rather than assuming either answer.
        /// </summary>
        private (string? TableQualifiedName, bool? Indexed) ResolveIndexInfo(ColumnReferenceExpression columnRef)
        {
            if (_scopeStack.Count == 0)
            {
                return (null, null);
            }

            var scopeChain = _scopeStack.Select(s => ((IReadOnlyDictionary<string, ScopeEntry>)s.ByAlias, (IReadOnlyList<ScopeEntry>)s.Ordered)).ToList();
            var provenance = ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, ledger: null);

            return provenance switch
            {
                ColumnProvenance.BaseColumn baseColumn => (
                    baseColumn.TableQualifiedName,
                    catalog.Find(baseColumn.TableQualifiedName, _currentProcScope)?.IsIndexedColumn(baseColumn.ColumnName) ?? false),

                // A multi-statement TVF's own RETURNS TABLE(...) column has no real backing
                // table, so TableQualifiedName stays null there - but a trigger's inserted/
                // deleted DOES carry one (FromScopeResolver.ToPseudoTableRelation keeps the
                // real target table's name so the finding stays attributable to where the data
                // actually lives), so it must not be discarded the way TypedPredicateExtractor's
                // identical Declared case doesn't discard it either. Indexed is always false
                // regardless: neither shape is backed by a real catalog index.
                ColumnProvenance.Declared declared => (declared.TableQualifiedName, false),

                _ => (null, null),
            };
        }

        private void PushCteScope(WithCtesAndXmlNamespaces? withClause)
        {
            var currentCtes = CurrentCteRelations();
            var ctes = CteResolver.Resolve(withClause, catalog, resolvedViews, sourcePath, ledger: null, _currentProcScope);
            _cteStack.Push(ctes.Count == 0 ? currentCtes : MergeCtes(currentCtes, ctes));
        }

        private IReadOnlyDictionary<string, ResolvedRelation> CurrentCteRelations() =>
            _cteStack.Count > 0 ? _cteStack.Peek() : EmptyResolvedViews;

        private FromScopeResolver.ResolutionContext CurrentResolutionContext() =>
            new(catalog, resolvedViews, sourcePath, Ledger: null, CurrentCteRelations(), _currentProcScope);

        private static Dictionary<string, ResolvedRelation> MergeCtes(
            IReadOnlyDictionary<string, ResolvedRelation> outer, IReadOnlyDictionary<string, ResolvedRelation> inner)
        {
            var merged = new Dictionary<string, ResolvedRelation>(outer, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, relation) in inner)
            {
                merged[name] = relation;
            }

            return merged;
        }
    }
}
