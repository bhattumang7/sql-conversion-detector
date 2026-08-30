using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class AggregateDivisionColumnstoreScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    private static readonly HashSet<string> AggregateFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "AVG", "COUNT", "COUNT_BIG", "MIN", "MAX",
    };

    public static IReadOnlyList<AggregateDivisionColumnstoreFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null, rules: [rule]);
        parseResult.Fragment.Accept(walker);
    return Harvest(rule);
    }
    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<AggregateDivisionColumnstoreFinding> Harvest(Rule rule) =>
            [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];


    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        public List<AggregateDivisionColumnstoreFinding> Findings { get; } = [];

        public void OnEnterQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, ModuleWalker walker)
        {
            var ordered = scopeChain[0].Ordered;
            var tables = ordered
                .Where(e => !e.IsViewLayer && e.Relation.QualifiedName is not null)
                .Select(e => e.Relation.QualifiedName!)
                .Distinct(catalog.IdentifierComparer)
                .Select(name => catalog.Find(name))
                .Where(t => t is not null && t.Kind == CatalogTableKind.Table)
                .Select(t => t!)
                .ToList();

            if (tables.Count > 0 && tables.Any(t => t.Indexes.Any(ix => ix.IsColumnstore)))
            {
                var columnstoreTable = tables.First(t => t.Indexes.Any(ix => ix.IsColumnstore));
                foreach (var element in node.SelectElements.OfType<SelectScalarExpression>())
                {
                    InspectTopLevel(element.Expression, columnstoreTable);
                }

                if (node.HavingClause?.SearchCondition is { } having)
                {
                    InspectTopLevel(having, columnstoreTable);
                }
            }
        }

        private void InspectTopLevel(TSqlFragment root, CatalogTable columnstoreTable)
        {
            var collector = new AggregateCallCollector();
            root.Accept(collector);
            foreach (var call in collector.Calls)
            {
                InspectAggregateCall(call, columnstoreTable);
            }
        }

        private void InspectAggregateCall(FunctionCall call, CatalogTable columnstoreTable)
        {
            foreach (var parameter in call.Parameters)
            {
                var caseCollector = new CaseExpressionCollector();
                parameter.Accept(caseCollector);
                if (caseCollector.CaseExpressions.Any(ContainsErrorProneDivision))
                {
                    Findings.Add(new AggregateDivisionColumnstoreFinding(
                        call.FunctionName.Value.ToUpperInvariant(), columnstoreTable.QualifiedName,
                        sourcePath, call.StartLine, call.StartColumn));
                }
            }
        }

        private static bool ContainsErrorProneDivision(CaseExpression caseExpression)
        {
            var resultExpressions = caseExpression switch
            {
                SimpleCaseExpression simple => simple.WhenClauses.Select(w => w.ThenExpression)
                    .Append(simple.ElseExpression),
                SearchedCaseExpression searched => searched.WhenClauses.Select(w => w.ThenExpression)
                    .Append(searched.ElseExpression),
                _ => [],
            };

            foreach (var result in resultExpressions)
            {
                if (result is null)
                {
                    continue;
                }

                var divisionCollector = new DivisionCollector();
                result.Accept(divisionCollector);
                if (divisionCollector.Divisions.Any(d => d.SecondExpression is not Literal))
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class AggregateCallCollector : TSqlFragmentVisitor
        {
            public List<FunctionCall> Calls { get; } = [];

            public override void ExplicitVisit(FunctionCall node)
            {
                if (AggregateFunctionNames.Contains(node.FunctionName.Value))
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

        private sealed class CaseExpressionCollector : TSqlFragmentVisitor
        {
            public List<CaseExpression> CaseExpressions { get; } = [];

            public override void ExplicitVisit(SimpleCaseExpression node)
            {
                CaseExpressions.Add(node);
                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(SearchedCaseExpression node)
            {
                CaseExpressions.Add(node);
                base.ExplicitVisit(node);
            }
        }

        private sealed class DivisionCollector : TSqlFragmentVisitor
        {
            public List<BinaryExpression> Divisions { get; } = [];

            public override void ExplicitVisit(BinaryExpression node)
            {
                if (node.BinaryExpressionType == BinaryExpressionType.Divide)
                {
                    Divisions.Add(node);
                }

                base.ExplicitVisit(node);
            }
        }
    }
}
