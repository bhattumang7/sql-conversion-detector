using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class MultiReferencedCteScanner
{
    public static IReadOnlyList<MultiReferencedCteFinding> Scan(SqlParseResult parseResult, DatabaseCatalog? catalog = null)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog ?? new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
    return Harvest(rule);
    }
    internal static Rule CreateRule(string sourcePath, DatabaseCatalog? catalog = null) => new(sourcePath, catalog?.IdentifierComparer ?? StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<MultiReferencedCteFinding> Harvest(Rule rule) =>
            [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line),
        ];


    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal sealed class Rule(string sourcePath, StringComparer identifierComparer) : IModuleRule
    {
        public List<MultiReferencedCteFinding> Findings { get; } = [];

        public void OnEnterSelectStatementScope(SelectStatement node, ModuleWalker walker)
        {
            InspectWithClause(node.WithCtesAndXmlNamespaces, node.QueryExpression);
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

        private List<int> CollectReferences(TSqlFragment fragment, string cteName)
        {
            var collector = new ReferenceCollector(cteName, identifierComparer);
            fragment.Accept(collector);
            return collector.Lines;
        }

        private sealed class ReferenceCollector(string cteName, StringComparer identifierComparer) : TSqlFragmentVisitor
        {
            public List<int> Lines { get; } = [];

            public override void ExplicitVisit(NamedTableReference node)
            {
                if (node.SchemaObject.SchemaIdentifier is null && identifierComparer.Equals(node.SchemaObject.BaseIdentifier.Value, cteName))
                {
                    Lines.Add(node.StartLine);
                }

                base.ExplicitVisit(node);
            }
        }
    }
}
