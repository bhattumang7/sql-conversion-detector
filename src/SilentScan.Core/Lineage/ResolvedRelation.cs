namespace SilentScan.Core.Lineage;

public sealed record ResolvedRelation(string? QualifiedName, IReadOnlyList<ResolvedColumn> Columns)
{
    public static readonly ResolvedRelation Empty = new(QualifiedName: null, Columns: []);

    public ResolvedColumn? FindColumn(string columnName, StringComparer? identifierComparer = null)
    {
        var comparer = identifierComparer ?? StringComparer.OrdinalIgnoreCase;
        return Columns.FirstOrDefault(c => comparer.Equals(c.Name, columnName));
    }
}
