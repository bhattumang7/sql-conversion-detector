namespace SilentScan.Core.Catalog;

public static class MaskingFunctionNameNormalizer
{
    public static string? Normalize(string? rawLiteral)
    {
        if (string.IsNullOrWhiteSpace(rawLiteral))
        {
            return null;
        }

        var parenIndex = rawLiteral.IndexOf('(');
        var name = parenIndex >= 0 ? rawLiteral[..parenIndex] : rawLiteral;
        return name.Trim().ToLowerInvariant();
    }
}
