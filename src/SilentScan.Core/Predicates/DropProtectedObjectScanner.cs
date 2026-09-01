using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

public static class DropProtectedObjectScanner
{
    private static readonly HashSet<string> FixedDatabaseRoleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "db_owner",
        "db_accessadmin",
        "db_securityadmin",
        "db_ddladmin",
        "db_backupoperator",
        "db_datareader",
        "db_datawriter",
        "db_denydatareader",
        "db_denydatawriter",
    };

    public static IReadOnlyList<DropProtectedObjectFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<DropProtectedObjectFinding>();

        foreach (var dropSchema in catalog.DropSchemaEvents)
        {
            if (catalog.SchemaOwnsAnyKnownObject(dropSchema.SchemaName))
            {
                findings.Add(new DropProtectedObjectFinding(
                    DropProtectedObjectKind.SchemaNotEmpty, dropSchema.SchemaName,
                    dropSchema.SourcePath, dropSchema.SourceLine, dropSchema.SourceColumn));
            }
        }

        foreach (var dropRole in catalog.DropRoleEvents)
        {
            if (FixedDatabaseRoleNames.Contains(dropRole.RoleName))
            {
                findings.Add(new DropProtectedObjectFinding(
                    DropProtectedObjectKind.FixedDatabaseRole, dropRole.RoleName,
                    dropRole.SourcePath, dropRole.SourceLine, dropRole.SourceColumn));
            }
        }

        return
        [
            .. findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }
}
