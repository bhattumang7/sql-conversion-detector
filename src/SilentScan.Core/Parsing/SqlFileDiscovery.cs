namespace SilentScan.Core.Parsing;

public static class SqlFileDiscovery
{
    private static readonly IReadOnlyList<string> DefaultExtensions = [".sql"];

public static IReadOnlyList<string> EnumerateSqlFiles(string rootPath, IReadOnlyList<string>? extensions = null)
    {
        if (File.Exists(rootPath))
        {
            return [rootPath];
        }

        var normalizedExtensions = extensions is { Count: > 0 }
            ? extensions.Select(NormalizeExtension).ToList()
            : DefaultExtensions;

        return Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .Where(path => normalizedExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    private static string NormalizeExtension(string extension) =>
        extension.StartsWith('.') ? extension : $".{extension}";
}
