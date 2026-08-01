namespace SilentScan.Core.Parsing;

/// <summary>Finds SQL script files under a root. Ordering is deterministic (CLAUDE.md).</summary>
public static class SqlFileDiscovery
{
    private static readonly IReadOnlyList<string> DefaultExtensions = [".sql"];

    /// <summary>
    /// <paramref name="extensions"/> defaults to .sql only. Real corpora don't always use it -
    /// DNN Platform ships DDL as .SqlDataProvider, for example (docs/audit-remediation-plan.md
    /// Phase 6.1) - so a bare `scan &lt;folder&gt;` run against such a repo would otherwise find
    /// nothing and report a clean, empty, misleading scan rather than an honest "0 files
    /// examined."
    /// </summary>
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
