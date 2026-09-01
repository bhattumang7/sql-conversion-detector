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

    public bool IsAccentInsensitive =>
        Name.EndsWith("_AI", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("_AI_", StringComparison.OrdinalIgnoreCase);

    public bool GuaranteesDistinctLiteralsAreUnequal => IsCaseSensitive && !IsAccentInsensitive;

    public bool IsUtf8 => Name.EndsWith("_UTF8", StringComparison.OrdinalIgnoreCase);

    public bool IsSupplementaryCharacterAware =>
        Name.EndsWith("_SC", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("_SC_", StringComparison.OrdinalIgnoreCase);

    public static StringComparer IdentifierComparer(Collation? collation) =>
        collation is { IsCaseSensitive: true } ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
}
