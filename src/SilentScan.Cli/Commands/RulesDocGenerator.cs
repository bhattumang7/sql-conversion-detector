using SilentScan.Core.Reporting;

namespace SilentScan.Cli.Commands;

/// <summary>
/// The filesystem side of <c>rules-doc</c>: reads every fixture <see cref="RuleCatalog"/> names
/// (plus, where CLAUDE.md's <c>RULEID_fires.sql</c>/<c>RULEID_clean.sql</c> convention left one
/// on disk, its clean sibling) and hands the text to <see cref="RuleCatalogHtmlWriter"/>/
/// <see cref="RulePageHtmlWriter"/>, which stay filesystem-free by design. Also prunes any
/// <c>docs/rules/*.html</c> file whose slug no longer belongs to a real catalog rule, so a
/// renamed/removed rule can't leave an orphaned page behind. Extracted from
/// <see cref="RulesDocCommand"/> so a docs-are-current regeneration test can call it directly.
/// </summary>
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

        var prunedCount = 0;
        foreach (var existing in Directory.EnumerateFiles(rulesDir, "*.html"))
        {
            if (!expectedFileNames.Contains(Path.GetFileName(existing)))
            {
                File.Delete(existing);
                prunedCount++;
            }
        }

        return prunedCount;
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
