namespace SilentScan.Core.Lineage;

public sealed record ResolvedRelation(string? QualifiedName, IReadOnlyList<ResolvedColumn> Columns)
{
    public static readonly ResolvedRelation Empty = new(QualifiedName: null, Columns: []);

    public ResolvedColumn? FindColumn(string columnName) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
}
