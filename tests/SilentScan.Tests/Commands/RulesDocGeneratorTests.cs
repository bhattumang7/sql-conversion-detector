using SilentScan.Cli.Commands;

namespace SilentScan.Tests.Commands;

/// <summary>
/// Docs-are-current regeneration test for CLAUDE.md's "never hand-edit docs/rules.html" rule -
/// regenerates the index and every per-rule page into a temp directory and byte-compares against
/// the committed <c>docs/</c> tree. Drift (a catalog edit that never re-ran <c>rules-doc</c>) fails
/// the build instead of silently going stale.
/// </summary>
public sealed class RulesDocGeneratorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"silentscan-rules-doc-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void WriteAll_RegeneratedAgainstRepoRoot_MatchesCommittedDocs()
    {
        var repoRoot = FindRepoRoot() ?? throw new InvalidOperationException("Could not locate repo root from test output directory.");
        var tempIndexPath = Path.Combine(_tempDir, "rules.html");
        var tempRulesDir = Path.Combine(_tempDir, "rules");

        RulesDocGenerator.WriteAll(repoRoot, tempIndexPath, tempRulesDir);

        var committedIndexPath = Path.Combine(repoRoot, "docs", "rules.html");
        var committedRulesDir = Path.Combine(repoRoot, "docs", "rules");

        Assert.Equal(File.ReadAllText(committedIndexPath), File.ReadAllText(tempIndexPath));

        var expectedFiles = Directory.GetFiles(committedRulesDir, "*.html").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var actualFiles = Directory.GetFiles(tempRulesDir, "*.html").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedFiles, actualFiles);

        foreach (var fileName in expectedFiles)
        {
            Assert.Equal(
                File.ReadAllText(Path.Combine(committedRulesDir, fileName!)),
                File.ReadAllText(Path.Combine(tempRulesDir, fileName!)));
        }
    }

    [Fact]
    public void WriteAll_OrphanedPageInRulesDir_IsPruned()
    {
        var repoRoot = FindRepoRoot() ?? throw new InvalidOperationException("Could not locate repo root from test output directory.");
        var tempIndexPath = Path.Combine(_tempDir, "rules.html");
        var tempRulesDir = Path.Combine(_tempDir, "rules");
        Directory.CreateDirectory(tempRulesDir);
        var orphanPath = Path.Combine(tempRulesDir, "no-longer-a-real-rule.html");
        File.WriteAllText(orphanPath, "stale");

        var prunedCount = RulesDocGenerator.WriteAll(repoRoot, tempIndexPath, tempRulesDir);

        Assert.Equal(1, prunedCount);
        Assert.False(File.Exists(orphanPath));
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SilentScan.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
