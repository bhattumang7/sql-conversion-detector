using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

public static class NonPersistedComputedColumnScanner
{
    public static IReadOnlyList<NonPersistedComputedColumnFinding> Scan(DatabaseCatalog catalog)
    {
        var definitions = catalog.SchemaExpressions
            .Where(e => e.Kind == SchemaDependencyKind.ComputedColumn)
            .ToLookup(e => (e.TableQualifiedName, ColumnName: e.ColumnName ?? string.Empty), TupleComparer.Instance);

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

    private sealed class TupleComparer : IEqualityComparer<(string TableQualifiedName, string ColumnName)>
    {
        public static readonly TupleComparer Instance = new();

        public bool Equals((string TableQualifiedName, string ColumnName) x, (string TableQualifiedName, string ColumnName) y) =>
            string.Equals(x.TableQualifiedName, y.TableQualifiedName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.ColumnName, y.ColumnName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string TableQualifiedName, string ColumnName) obj) =>
            HashCode.Combine(
                obj.TableQualifiedName.ToUpperInvariant(),
                obj.ColumnName.ToUpperInvariant());
    }
}
