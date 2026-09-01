using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class UnpivotExactTypeMismatchScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<UnpivotExactTypeMismatchFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<UnpivotExactTypeMismatchFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.MismatchedColumnName, StringComparer.Ordinal),
        ];

    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        public List<UnpivotExactTypeMismatchFinding> Findings { get; } = [];

        public void OnEnterUnpivotedTableReference(UnpivotedTableReference node, ModuleWalker walker)
        {
            if (node.TableReference is not NamedTableReference named
                || named.SchemaObject.ServerIdentifier is { Value.Length: > 0 })
            {
                return;
            }

            var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(named.SchemaObject));
            var table = catalog.Find(qualifiedName);
            if (table is null)
            {
                return;
            }

            var columns = new List<CatalogColumn>();
            foreach (var inColumn in node.InColumns)
            {
                var columnName = inColumn.MultiPartIdentifier.Identifiers[^1].Value;
                var resolved = table.FindColumn(columnName, catalog.IdentifierComparer);
                if (resolved?.Type is null)
                {
                    return;
                }

                columns.Add(resolved);
            }

            if (columns.Count < 2)
            {
                return;
            }

            var reference = columns[0];
            foreach (var column in columns.Skip(1))
            {
                if (column.Type == reference.Type)
                {
                    continue;
                }

                Findings.Add(new UnpivotExactTypeMismatchFinding(
                    table.QualifiedName, reference.Name, reference.Type!.ToString(), column.Name, column.Type!.ToString(),
                    sourcePath, node.StartLine, node.StartColumn));
            }
        }
    }
}
