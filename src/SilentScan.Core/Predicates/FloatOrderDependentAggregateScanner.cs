using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class FloatOrderDependentAggregateScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    private static readonly HashSet<string> OrderDependentAggregateFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "AVG", "VAR", "VARP", "STDEV", "STDEVP",
    };

    public static IReadOnlyList<FloatOrderDependentAggregateFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
    return Harvest(rule);
    }
    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<FloatOrderDependentAggregateFinding> Harvest(Rule rule) =>
            [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];


    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        public List<FloatOrderDependentAggregateFinding> Findings { get; } = [];

        public void OnEnterQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, ModuleWalker walker)
        {
            foreach (var element in node.SelectElements.OfType<SelectScalarExpression>())
            {
                Inspect(element.Expression, scopeChain);
            }

            if (node.HavingClause?.SearchCondition is { } having)
            {
                Inspect(having, scopeChain);
            }
        }

        private void Inspect(TSqlFragment root, ScopeChain scopeChain)
        {
            var collector = new AggregateCallCollector();
            root.Accept(collector);
            foreach (var call in collector.Calls)
            {
                InspectAggregateCall(call, scopeChain);
            }
        }

        private void InspectAggregateCall(FunctionCall call, ScopeChain scopeChain)
        {
            foreach (var parameter in call.Parameters)
            {
                if (parameter is ColumnReferenceExpression)
                {
                    if (BaseColumnResolver.ResolveBaseColumn(parameter, sourcePath, scopeChain, catalog) is { } directColumn
                        && directColumn.Type?.Category is SqlTypeCategory.Real or SqlTypeCategory.Float)
                    {
                        AddFinding(directColumn.TableQualifiedName, directColumn.ColumnName, directColumn.Type, call);
                    }

                    continue;
                }

                if (!AllReferencedColumnsResolveDirectly(parameter, scopeChain))
                {
                    continue;
                }

                var expressionType = ScalarExpressionResolver.ResolveScalarType(
                    parameter, scopeChain, sourcePath,
                    new ScalarExpressionResolver.ScalarTypeContext(Ledger: null, catalog.TypeAliases, catalog));

                if (expressionType?.Category is not (SqlTypeCategory.Real or SqlTypeCategory.Float))
                {
                    continue;
                }

                var referencedColumns = new HashSet<(string Table, string Column)>();
                parameter.Accept(new BaseColumnResolver.ColumnReferenceCollector(sourcePath, scopeChain, referencedColumns, catalog));

                var (tableQualifiedName, columnName) = referencedColumns.Count == 1
                    ? referencedColumns.Single()
                    : ("?", FragmentTextRenderer.Render(parameter));

                AddFinding(tableQualifiedName, columnName, expressionType, call);
            }
        }

        private bool AllReferencedColumnsResolveDirectly(ScalarExpression parameter, ScopeChain scopeChain)
        {
            var guard = new DirectColumnGuardVisitor(sourcePath, scopeChain, catalog);
            parameter.Accept(guard);
            return guard.AllDirect;
        }

        private void AddFinding(string tableQualifiedName, string columnName, SqlType type, FunctionCall call) =>
            Findings.Add(new FloatOrderDependentAggregateFinding(
                tableQualifiedName,
                columnName,
                type.ToString(),
                call.FunctionName.Value.ToUpperInvariant(),
                sourcePath,
                call.StartLine,
                call.StartColumn));

        private sealed class DirectColumnGuardVisitor(string sourcePath, ScopeChain scopeChain, DatabaseCatalog catalog) : TSqlFragmentVisitor
        {
            public bool AllDirect { get; private set; } = true;

            public override void ExplicitVisit(ColumnReferenceExpression node)
            {
                if (node.ColumnType != ColumnType.Wildcard
                    && BaseColumnResolver.ResolveBaseColumn(node, sourcePath, scopeChain, catalog) is null)
                {
                    AllDirect = false;
                }

                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(ScalarSubquery node)
            {
                _ = node;
            }
        }

        private sealed class AggregateCallCollector : TSqlFragmentVisitor
        {
            public List<FunctionCall> Calls { get; } = [];

            public override void ExplicitVisit(FunctionCall node)
            {
                if (node.OverClause is null && OrderDependentAggregateFunctionNames.Contains(node.FunctionName.Value))
                {
                    Calls.Add(node);
                }

                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(QuerySpecification node)
            {
                _ = node;
            }
        }
    }
}
