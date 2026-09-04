using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class ChangeTrackingEncryptedPrimaryKeyScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<ChangeTrackingEncryptedPrimaryKeyFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<ChangeTrackingEncryptedPrimaryKeyFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        public List<ChangeTrackingEncryptedPrimaryKeyFinding> Findings { get; } = [];

        public void OnEnterAlterTableChangeTrackingModificationStatement(AlterTableChangeTrackingModificationStatement node, ModuleWalker walker)
        {
            if (!node.IsEnable)
            {
                return;
            }

            var tableName = SchemaObjectNameHelper.Qualify(node.SchemaObjectName);
            if (catalog.Find(tableName) is not { } table)
            {
                return;
            }

            var primaryKey = table.Indexes.FirstOrDefault(i => i.Kind == CatalogIndexKind.PrimaryKey);
            if (primaryKey is null)
            {
                return;
            }

            foreach (var columnName in primaryKey.KeyColumns)
            {
                if (table.FindColumn(columnName, catalog.IdentifierComparer) is not { EncryptionType: not Catalog.ColumnEncryptionType.None })
                {
                    continue;
                }

                Findings.Add(new ChangeTrackingEncryptedPrimaryKeyFinding(
                    tableName, columnName, sourcePath, node.StartLine, node.StartColumn));
            }
        }
    }
}
