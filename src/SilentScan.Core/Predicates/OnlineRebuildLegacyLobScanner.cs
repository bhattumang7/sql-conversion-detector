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

        foreach (var drop in catalog.DropIndexOnlineEvents)
        {
            findings.AddRange(ScanEvent(
                catalog, OnlineRebuildLegacyLobKind.DropIndexOnline, drop.TableQualifiedName,
                drop.SourcePath, drop.SourceLine, drop.SourceColumn));
        }

        foreach (var alter in catalog.AlterColumnEvents)
        {
            if (!alter.IsOnline)
            {
                continue;
            }

            SqlType? lobType;
            if (IsLegacyLob(alter.PreviousType))
            {
                lobType = alter.PreviousType;
            }
            else if (IsLegacyLob(alter.NewType))
            {
                lobType = alter.NewType;
            }
            else
            {
                lobType = null;
            }

            if (lobType is null)
            {
                continue;
            }

            findings.Add(new OnlineRebuildLegacyLobFinding(
                OnlineRebuildLegacyLobKind.AlterColumnOnline, alter.TableQualifiedName, alter.ColumnName,
                lobType.ToString()!, alter.SourcePath, alter.SourceLine, alter.SourceColumn));
        }

        return
        [
            .. findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.ColumnName, StringComparer.Ordinal),
        ];
    }

    private static bool IsLegacyLob(SqlType? type) => type is not null && LegacyLobCategories.Contains(type.Category);

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
