using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class FloatEqualityPredicateScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<FloatEqualityFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
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

    private sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        public List<FloatEqualityFinding> Findings { get; } = [];

        public void OnEnterQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, ModuleWalker walker) =>
            ModuleWalker.InspectAllPredicateLocations(node, scopeChain, Inspect);

        public void OnEnterUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            ModuleWalker.InspectAllPredicateLocations(node, scopeChain, Inspect);

        public void OnEnterDeleteStatementScope(DeleteStatement node, ScopeChain scopeChain, ModuleWalker walker) =>
            ModuleWalker.InspectAllPredicateLocations(node, scopeChain, Inspect);

        private void Inspect(BooleanExpression searchCondition, ScopeChain scopeChain)
        {
            var collector = new EqualityCollector();
            searchCondition.Accept(collector);
            foreach (var comparison in collector.Comparisons)
            {
                InspectEquality(comparison, scopeChain);
            }
        }

        private void InspectEquality(BooleanComparisonExpression comparison, ScopeChain scopeChain)
        {
            foreach (var side in new[] { comparison.FirstExpression, comparison.SecondExpression })
            {
                if (side is not ColumnReferenceExpression columnRef
                    || BaseColumnResolver.ResolveBaseColumn(columnRef, sourcePath, scopeChain, catalog) is not { } resolved
                    || resolved.Type?.Category is not (SqlTypeCategory.Real or SqlTypeCategory.Float))
                {
                    continue;
                }

                Findings.Add(new FloatEqualityFinding(
                    resolved.TableQualifiedName,
                    resolved.ColumnName,
                    resolved.Type!.ToString(),
                    sourcePath,
                    comparison.StartLine,
                    comparison.StartColumn));

                return;
            }
        }

        private sealed class EqualityCollector : TSqlFragmentVisitor
        {
            public List<BooleanComparisonExpression> Comparisons { get; } = [];

            public override void ExplicitVisit(BooleanComparisonExpression node)
            {
                if (node.ComparisonType == BooleanComparisonType.Equals)
                {
                    Comparisons.Add(node);
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
