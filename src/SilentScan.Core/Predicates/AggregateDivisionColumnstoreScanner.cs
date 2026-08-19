using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second full-archive practitioner sweep" §G: "Aggregate argument
/// containing a division ... that relies on short-circuit elimination, on a table with a
/// columnstore or batch-mode-eligible index" - see <see cref="AggregateDivisionColumnstoreFinding"/>
/// for the full scope/precision story, including the honest live-reproduction attempt and why this
/// ships as a structural risk flag only.
/// </summary>
public static class AggregateDivisionColumnstoreScanner
{
    private static readonly HashSet<string> AggregateFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "AVG", "COUNT", "COUNT_BIG", "MIN", "MAX",
    };

    public static IReadOnlyList<AggregateDivisionColumnstoreFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        // File-wide, not per-statement: a CTE declared anywhere in this file shadows a same-named
        // real base table for ITS OWN statement's lifetime, but this coarser set only ever causes
        // an extra decline (a real table in a DIFFERENT statement sharing a CTE's name elsewhere
        // in the file), never a false positive - the safe direction CLAUDE.md's precision-first
        // rule asks for, and far simpler than per-statement CTE-scope tracking for a scanner with
        // none today (2026-08 audit: DirectBaseTableResolver never consulted CTE scope at all).
        var cteNames = CteNameCollector.Collect(parseResult.Fragment);
        var visitor = new Visitor(parseResult.SourcePath, catalog, cteNames);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog, IReadOnlySet<string> cteNames) : TSqlFragmentVisitor
    {
        public List<AggregateDivisionColumnstoreFinding> Findings { get; } = [];

        public override void ExplicitVisit(QuerySpecification node)
        {
            var tables = DirectBaseTableResolver.ResolveDirectBaseTables(catalog, node.FromClause?.TableReferences, cteNames);
            if (tables.Count > 0 && tables.Values.Any(t => t.Indexes.Any(ix => ix.IsColumnstore)))
            {
                var columnstoreTable = tables.Values.First(t => t.Indexes.Any(ix => ix.IsColumnstore));
                foreach (var element in node.SelectElements.OfType<SelectScalarExpression>())
                {
                    InspectTopLevel(element.Expression, columnstoreTable);
                }

                if (node.HavingClause?.SearchCondition is { } having)
                {
                    InspectTopLevel(having, columnstoreTable);
                }
            }

            base.ExplicitVisit(node);
        }

        /// <summary>
        /// Finds every aggregate function call reachable from <paramref name="root"/> WITHOUT
        /// descending into a nested <see cref="QuerySpecification"/> (own FROM scope, reached
        /// separately), matching <see cref="FloatEqualityPredicateScanner"/>'s own precedent.
        /// </summary>
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

        /// <summary>
        /// True when any THEN/ELSE result expression of <paramref name="caseExpression"/> contains
        /// a division whose divisor is not a literal constant (a literal divisor can never be zero,
        /// so is not error-prone regardless of execution mode). Does not descend into a NESTED CASE
        /// expression's own guard/result expressions differently - a division anywhere inside a
        /// result expression counts, including one reached through further nesting.
        /// </summary>
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
                // Deliberately does not call base.ExplicitVisit(node) - a nested subquery's own
                // aggregate is reached separately, with its own correct FROM scope, by the outer
                // visitor's own QuerySpecification traversal.
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
