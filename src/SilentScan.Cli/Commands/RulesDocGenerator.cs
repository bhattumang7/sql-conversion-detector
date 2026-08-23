using SilentScan.Core.Reporting;

namespace SilentScan.Cli.Commands;

public static class RulesDocGenerator
{
    public static int WriteAll(string repoRoot, string indexPath, string rulesDir)
    {
        Directory.CreateDirectory(rulesDir);

        File.WriteAllText(indexPath, RuleCatalogHtmlWriter.Write());

        var expectedFileNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in RuleCatalog.BaseRules)
        {
            var examples = rule.Examples.Select(firesPath => ReadExample(repoRoot, firesPath)).ToList();
            var fileName = RuleDocSite.Slug(rule.Id) + ".html";
            expectedFileNames.Add(fileName);
            File.WriteAllText(Path.Combine(rulesDir, fileName), RulePageHtmlWriter.Write(rule, examples));
        }

        var stale = Directory.EnumerateFiles(rulesDir, "*.html")
            .Where(existing => !expectedFileNames.Contains(Path.GetFileName(existing)))
            .ToList();
        foreach (var existing in stale)
        {
            File.Delete(existing);
        }

        return stale.Count;
    }

    private static RuleExample ReadExample(string repoRoot, string firesRelativePath)
    {
        var firesFullPath = Path.Combine(repoRoot, firesRelativePath);
        var firesSql = File.ReadAllText(firesFullPath);

        var cleanRelativePath = firesRelativePath.Replace("_fires.sql", "_clean.sql", StringComparison.Ordinal);
        var cleanFullPath = Path.Combine(repoRoot, cleanRelativePath);
        var hasClean = cleanRelativePath != firesRelativePath && File.Exists(cleanFullPath);

        return new RuleExample(
            firesRelativePath,
            firesSql,
            hasClean ? cleanRelativePath : null,
            hasClean ? File.ReadAllText(cleanFullPath) : null);
    }
}
