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

        findings.AddRange(catalog.DropSchemaEvents
            .Where(dropSchema => catalog.SchemaOwnsAnyKnownObject(dropSchema.SchemaName))
            .Select(dropSchema => new DropProtectedObjectFinding(
                DropProtectedObjectKind.SchemaNotEmpty, dropSchema.SchemaName,
                dropSchema.SourcePath, dropSchema.SourceLine, dropSchema.SourceColumn)));

        findings.AddRange(catalog.DropRoleEvents
            .Where(dropRole => FixedDatabaseRoleNames.Contains(dropRole.RoleName))
            .Select(dropRole => new DropProtectedObjectFinding(
                DropProtectedObjectKind.FixedDatabaseRole, dropRole.RoleName,
                dropRole.SourcePath, dropRole.SourceLine, dropRole.SourceColumn)));

        return
        [
            .. findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }
}
