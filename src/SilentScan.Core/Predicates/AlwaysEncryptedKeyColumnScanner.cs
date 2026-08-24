using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

public static class AlwaysEncryptedKeyColumnScanner
{
    public static IReadOnlyList<AlwaysEncryptedKeyColumnFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<AlwaysEncryptedKeyColumnFinding>();

        foreach (var table in catalog.Tables)
        {
            foreach (var index in table.Indexes)
            {
                findings.AddRange(ScanKeyColumns(table, index.Name ?? "(unnamed)", KindOf(index.Kind), index.KeyColumns));
            }

            foreach (var statistics in table.EffectiveStatistics)
            {
                findings.AddRange(ScanKeyColumns(table, statistics.Name, AlwaysEncryptedKeyColumnKind.Statistics, statistics.KeyColumns));
            }
        }

        return
        [
            .. findings
                .OrderBy(f => f.TableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.ObjectName, StringComparer.Ordinal)
                .ThenBy(f => f.ColumnName, StringComparer.Ordinal),
        ];
    }

    private static AlwaysEncryptedKeyColumnKind KindOf(CatalogIndexKind kind) => kind switch
    {
        CatalogIndexKind.PrimaryKey => AlwaysEncryptedKeyColumnKind.PrimaryKey,
        CatalogIndexKind.UniqueConstraint => AlwaysEncryptedKeyColumnKind.UniqueConstraint,
        _ => AlwaysEncryptedKeyColumnKind.Index,
    };

    private static IEnumerable<AlwaysEncryptedKeyColumnFinding> ScanKeyColumns(
        CatalogTable table, string objectName, AlwaysEncryptedKeyColumnKind kind, IReadOnlyList<string> keyColumns)
    {
        foreach (var columnName in keyColumns)
        {
            if (table.FindColumn(columnName) is not { EncryptionType: ColumnEncryptionType.Randomized, EnclaveSupport: ColumnEncryptionEnclaveSupport.Disabled })
            {
                continue;
            }

            yield return new AlwaysEncryptedKeyColumnFinding(table.QualifiedName, objectName, kind, columnName, table.SourcePath, table.SourceLine);
        }
    }
}
