namespace SilentScan.Core.TypeInference;

public enum CollationSource
{
    ColumnExplicit,

    DatabaseDefaultFromDdl,

    DatabaseDefaultFromManifest,
}

public sealed record Collation(string Name, CollationSource Source = CollationSource.ColumnExplicit)
{
    public bool IsSqlFamily => Name.StartsWith("SQL_", StringComparison.OrdinalIgnoreCase);

    public bool IsWindowsFamily => !IsSqlFamily;

    public bool IsCaseSensitive =>
        Name.Contains("_CS_", StringComparison.OrdinalIgnoreCase)
        || Name.EndsWith("_BIN", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("_BIN_", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("_BIN2", StringComparison.OrdinalIgnoreCase);
}
