using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class OnlineRebuildLegacyLobScanner
{
    private static readonly HashSet<SqlTypeCategory> LegacyLobCategories =
    [
        SqlTypeCategory.NText,
        SqlTypeCategory.Text,
        SqlTypeCategory.Image,
    ];

    public static IReadOnlyList<OnlineRebuildLegacyLobFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<OnlineRebuildLegacyLobFinding>();

        foreach (var rebuild in catalog.AlterTableRebuildEvents)
        {
            findings.AddRange(ScanEvent(
                catalog, OnlineRebuildLegacyLobKind.AlterTableRebuild, rebuild.TableQualifiedName,
                rebuild.SourcePath, rebuild.SourceLine, rebuild.SourceColumn));
        }

        foreach (var rebuild in catalog.AlterIndexAllRebuildEvents)
        {
            findings.AddRange(ScanEvent(
                catalog, OnlineRebuildLegacyLobKind.AlterIndexAllRebuild, rebuild.TableQualifiedName,
                rebuild.SourcePath, rebuild.SourceLine, rebuild.SourceColumn));
        }

        return
        [
            .. findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.ColumnName, StringComparer.Ordinal),
        ];
    }

    private static IEnumerable<OnlineRebuildLegacyLobFinding> ScanEvent(
        DatabaseCatalog catalog, OnlineRebuildLegacyLobKind kind, string tableQualifiedName,
        string sourcePath, int line, int column)
    {
        var table = catalog.Find(tableQualifiedName);
        if (table is null)
        {
            yield break;
        }

        foreach (var col in table.Columns)
        {
            if (col.Type is not { } type || !LegacyLobCategories.Contains(type.Category))
            {
                continue;
            }

            yield return new OnlineRebuildLegacyLobFinding(
                kind, table.QualifiedName, col.Name, type.ToString(), sourcePath, line, column);
        }
    }
}
