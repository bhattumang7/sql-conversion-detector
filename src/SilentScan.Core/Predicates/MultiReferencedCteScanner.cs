using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "Lineage-metric findings" - "Multi-referenced CTE".
/// Syntax-only, no catalog/lineage dependency - a standalone scanner since counting references to
/// a WITH clause's own declared names across sibling AST regions (the main query body, and every
/// OTHER CTE's own body) is a different traversal shape than any existing per-comparison walk.
/// </summary>
public static class MultiReferencedCteScanner
{
    public static IReadOnlyList<MultiReferencedCteFinding> Scan(SqlParseResult parseResult)
    {
        var visitor = new Visitor(parseResult.SourcePath);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line),
        ];
    }

    private sealed class Visitor(string sourcePath) : TSqlFragmentVisitor
    {
        public List<MultiReferencedCteFinding> Findings { get; } = [];

        public override void ExplicitVisit(SelectStatement node)
        {
            InspectWithClause(node.WithCtesAndXmlNamespaces, node.QueryExpression);
            base.ExplicitVisit(node);
        }

        private void InspectWithClause(WithCtesAndXmlNamespaces? withClause, QueryExpression mainBody)
        {
            if (withClause is null)
            {
                return;
            }

            var ctes = withClause.CommonTableExpressions;

            foreach (var cte in ctes)
            {
                var name = cte.ExpressionName.Value;

                var referenceLines = CollectReferences(mainBody, name);
                foreach (var other in ctes.Where(other => !ReferenceEquals(other, cte)))
                {
                    referenceLines.AddRange(CollectReferences(other.QueryExpression, name));
                }

                if (referenceLines.Count >= 2)
                {
                    Findings.Add(new MultiReferencedCteFinding(
                        name, referenceLines.Count, [.. referenceLines.OrderBy(l => l)],
                        sourcePath, cte.StartLine));
                }
            }
        }

        private static List<int> CollectReferences(TSqlFragment fragment, string cteName)
        {
            var collector = new ReferenceCollector(cteName);
            fragment.Accept(collector);
            return collector.Lines;
        }

        private sealed class ReferenceCollector(string cteName) : TSqlFragmentVisitor
        {
            public List<int> Lines { get; } = [];

            public override void ExplicitVisit(NamedTableReference node)
            {
                if (node.SchemaObject.SchemaIdentifier is null && string.Equals(node.SchemaObject.BaseIdentifier.Value, cteName, StringComparison.OrdinalIgnoreCase))
                {
                    Lines.Add(node.StartLine);
                }

                base.ExplicitVisit(node);
            }
        }
    }
}
