using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class StringConcatNullScanner
{
    private static readonly HashSet<SqlTypeCategory> StringCategories =
    [
        SqlTypeCategory.Char, SqlTypeCategory.VarChar, SqlTypeCategory.NChar, SqlTypeCategory.NVarChar,
        SqlTypeCategory.Text, SqlTypeCategory.NText,
    ];

    public static IReadOnlyList<StringConcatNullFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = new Rule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    private enum LeafKind
    {
        Unknown,

        String,

        Guarded,
    }

    private readonly record struct Leaf(LeafKind Kind, bool IsNullableColumn, string? TableQualifiedName, string? ColumnName);

    private sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        public List<StringConcatNullFinding> Findings { get; } = [];

        public void OnEnterQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, ModuleWalker walker)
        {
            foreach (var element in node.SelectElements.OfType<SelectScalarExpression>())
            {
                InspectTopLevel(element.Expression, scopeChain);
            }
        }

        public void OnEnterUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, ModuleWalker walker)
        {
            var spec = node.UpdateSpecification;
            foreach (var setClause in spec.SetClauses.OfType<AssignmentSetClause>())
            {
                if (setClause.NewValue is ScalarExpression newValue)
                {
                    InspectTopLevel(newValue, scopeChain);
                }
            }
        }

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

                return;
            }

            if (!leaves.Any(l => l.Kind == LeafKind.String))
            {

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
                    var resolved = BaseColumnResolver.ResolveBaseColumn(columnRef, sourcePath, scopeChain, catalog);
                    if (resolved is not { } r || r.Type is not { } columnType || !StringCategories.Contains(columnType.Category))
                    {

                        return new Leaf(LeafKind.Unknown, false, null, null);
                    }

                    var isNullable = catalog.Find(r.TableQualifiedName)?.FindColumn(r.ColumnName, catalog.IdentifierComparer)?.IsNullable ?? false;
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

        private sealed class ConcatChainRootCollector : TSqlFragmentVisitor
        {
            public List<BinaryExpression> Roots { get; } = [];

            public override void ExplicitVisit(BinaryExpression node)
            {
                if (node.BinaryExpressionType == BinaryExpressionType.Add)
                {
                    Roots.Add(node);

                    return;
                }

                base.ExplicitVisit(node);
            }

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

            public override void ExplicitVisit(CoalesceExpression node)
            {
                _ = node;
            }

            public override void ExplicitVisit(QuerySpecification node)
            {
                _ = node;
            }
        }
    }
}
