using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class SemanticSearchScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<SemanticSearchFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<SemanticSearchFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        public List<SemanticSearchFinding> Findings { get; } = [];

        public void OnEnterSemanticTableReference(SemanticTableReference node, ModuleWalker walker)
        {
            if (node.TableName is null)
            {
                return;
            }

            var qualifiedName = SchemaObjectNameHelper.Qualify(node.TableName);
            var table = catalog.Find(qualifiedName);
            if (table is not { Kind: CatalogTableKind.Table })
            {
                return;
            }

            if (!table.HasFullTextIndex)
            {
                Findings.Add(new SemanticSearchFinding(
                    SemanticSearchFindingKind.TableNotSemanticFullTextIndexed, qualifiedName, ColumnName: null,
                    "this table has no full-text index at all",
                    sourcePath, node.StartLine, node.StartColumn));
                return;
            }

            var semanticColumns = table.SemanticFullTextColumnNames;
            if (semanticColumns is null)
            {
                return;
            }

            var referencedColumns = EnumerateReferencedColumns(node).Where(c => c.ColumnType != ColumnType.Wildcard).ToList();
            if (referencedColumns.Count == 0)
            {
                if (semanticColumns.Count == 0)
                {
                    Findings.Add(new SemanticSearchFinding(
                        SemanticSearchFindingKind.TableNotSemanticFullTextIndexed, qualifiedName, ColumnName: null,
                        "no full-text index column on this table is enabled with STATISTICAL_SEMANTICS",
                        sourcePath, node.StartLine, node.StartColumn));
                }

                return;
            }

            foreach (var columnRef in referencedColumns)
            {
                var columnName = columnRef.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value;
                if (columnName is null || table.FindColumn(columnName, catalog.IdentifierComparer) is null)
                {
                    continue;
                }

                if (!semanticColumns.Contains(columnName, catalog.IdentifierComparer))
                {
                    Findings.Add(new SemanticSearchFinding(
                        SemanticSearchFindingKind.ColumnNotSemanticFullTextIndexed, qualifiedName, columnName,
                        $"column '{columnName}' is not full-text indexed with STATISTICAL_SEMANTICS",
                        sourcePath, columnRef.StartLine, columnRef.StartColumn));
                }
            }
        }

        private static IEnumerable<ColumnReferenceExpression> EnumerateReferencedColumns(SemanticTableReference node)
        {
            foreach (var column in node.Columns)
            {
                yield return column;
            }

            if (node.MatchedColumn is not null)
            {
                yield return node.MatchedColumn;
            }
        }
    }
}
