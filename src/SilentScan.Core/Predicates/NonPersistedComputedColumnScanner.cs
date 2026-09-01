using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

public static class NonPersistedComputedColumnScanner
{
    public static IReadOnlyList<NonPersistedComputedColumnFinding> Scan(DatabaseCatalog catalog)
    {
        var definitions = catalog.SchemaExpressions
            .Where(e => e.Kind == SchemaDependencyKind.ComputedColumn)
            .ToLookup(e => (e.TableQualifiedName, ColumnName: e.ColumnName ?? string.Empty), new TupleComparer(catalog.IdentifierComparer));

        var findings = new List<NonPersistedComputedColumnFinding>();

        foreach (var table in catalog.Tables)
        {
            foreach (var column in table.Columns)
            {
                if (!column.IsComputed || column.IsPersisted)
                {
                    continue;
                }

                var definition = definitions[(table.QualifiedName, column.Name)].FirstOrDefault();

                findings.Add(new NonPersistedComputedColumnFinding(
                    table.QualifiedName,
                    column.Name,
                    definition?.DefinitionText ?? string.Empty,
                    table.IsColumnStoredInAnIndex(column.Name, catalog.IdentifierComparer),
                    definition?.SourcePath ?? table.SourcePath,
                    definition?.Line ?? table.SourceLine));
            }
        }

        return
        [
            .. findings
                .OrderBy(f => f.TableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.ColumnName, StringComparer.Ordinal),
        ];
    }

    private sealed class TupleComparer(StringComparer identifierComparer) : IEqualityComparer<(string TableQualifiedName, string ColumnName)>
    {
        public bool Equals((string TableQualifiedName, string ColumnName) x, (string TableQualifiedName, string ColumnName) y) =>
            identifierComparer.Equals(x.TableQualifiedName, y.TableQualifiedName) &&
            identifierComparer.Equals(x.ColumnName, y.ColumnName);

        public int GetHashCode((string TableQualifiedName, string ColumnName) obj) =>
            HashCode.Combine(
                identifierComparer.GetHashCode(obj.TableQualifiedName),
                identifierComparer.GetHashCode(obj.ColumnName));
    }
}
