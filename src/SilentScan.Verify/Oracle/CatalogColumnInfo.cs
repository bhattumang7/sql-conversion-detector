namespace SilentScan.Verify.Oracle;

public sealed record CatalogColumnInfo(
    string ColumnName,
    string TypeName,
    short MaxLength,
    byte Precision,
    byte Scale,
    string? CollationName,
    bool IsNullable);
