using SilentScan.Verify;
using SilentScan.Verify.Commands;

namespace SilentScan.Tests.Commands;

public sealed class VerifyCorpusCommandTests : IDisposable
{
    private readonly string _manifestPath = Path.Combine(Path.GetTempPath(), $"silentscan-verify-corpus-test-{Guid.NewGuid():N}.json");

    public VerifyCorpusCommandTests() =>
        File.WriteAllText(_manifestPath, """
            {
              "repos": [
                {
                  "name": "example",
                  "url": "https://github.com/example/example",
                  "commitSha": "abcdef0123456789abcdef0123456789abcdef01",
                  "license": "MIT",
                  "ddlPaths": ["db/schema/**/*.sql"]
                }
              ]
            }
            """);

    public void Dispose() => File.Delete(_manifestPath);

    [Fact]
    public async Task RunAsync_MissingManifest_ReturnsOneAndWritesError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await VerifyCorpusCommand.RunAsync(
            new VerifyCorpusCommand.VerifyCorpusOptions("/no/such/manifest.json", "corpus/_clones", RepoFilter: null, "high"),
            SqlServerOptions.LocalDocker, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("manifest not found", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_RepoFilterMatchesNoManifestEntry_ReturnsOneAndWritesError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await VerifyCorpusCommand.RunAsync(
            new VerifyCorpusCommand.VerifyCorpusOptions(_manifestPath, "corpus/_clones", RepoFilter: "no-such-repo", "high"),
            SqlServerOptions.LocalDocker, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("no manifest entry matches", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_RepoHasNoLocalClone_ReturnsOneAndWritesWarning()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await VerifyCorpusCommand.RunAsync(
            new VerifyCorpusCommand.VerifyCorpusOptions(_manifestPath, "/no/such/clones-root", RepoFilter: null, "high"),
            SqlServerOptions.LocalDocker, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("no local clone at", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_UnknownConfidence_ReturnsOneAndWritesError()
    {

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await VerifyCorpusCommand.RunAsync(
            new VerifyCorpusCommand.VerifyCorpusOptions(_manifestPath, "corpus/_clones", RepoFilter: null, "extreme"),
            SqlServerOptions.LocalDocker, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("unknown --confidence", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_RepoFilterDiffersOnlyByCase_StillMatchesManifestEntry()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await VerifyCorpusCommand.RunAsync(
            new VerifyCorpusCommand.VerifyCorpusOptions(_manifestPath, "corpus/_clones", RepoFilter: "EXAMPLE", "high"),
            SqlServerOptions.LocalDocker, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exitCode);
        var output = stderr.ToString();
        Assert.DoesNotContain("no manifest entry matches", output);
        Assert.Contains("no local clone at", output);
    }

    [Fact]
    public async Task RunAsync_RepoFilterSet_OnlyMatchingRepoIsProcessed()
    {
        var manifestPath = Path.Combine(Path.GetTempPath(), $"silentscan-verify-corpus-multi-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(manifestPath, """
            {
              "repos": [
                {
                  "name": "alpha-repo",
                  "url": "https://github.com/example/alpha-repo",
                  "commitSha": "abcdef0123456789abcdef0123456789abcdef01",
                  "license": "MIT",
                  "ddlPaths": ["db/schema/**/*.sql"]
                },
                {
                  "name": "beta-repo",
                  "url": "https://github.com/example/beta-repo",
                  "commitSha": "abcdef0123456789abcdef0123456789abcdef01",
                  "license": "MIT",
                  "ddlPaths": ["db/schema/**/*.sql"]
                }
              ]
            }
            """);
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await VerifyCorpusCommand.RunAsync(
                new VerifyCorpusCommand.VerifyCorpusOptions(manifestPath, "corpus/_clones", RepoFilter: "alpha-repo", "high"),
                SqlServerOptions.LocalDocker, stdout, stderr, CancellationToken.None);

            Assert.Equal(1, exitCode);
            var output = stderr.ToString();
            Assert.Contains("alpha-repo", output);
            Assert.DoesNotContain("beta-repo", output);
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }

    [Fact]
    public async Task RunAsync_RepoUrlHasTrailingSlash_CloneDirectoryNameDropsTheSlash()
    {
        var manifestPath = Path.Combine(Path.GetTempPath(), $"silentscan-verify-corpus-trailing-slash-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(manifestPath, """
            {
              "repos": [
                {
                  "name": "trailing-slash-repo",
                  "url": "https://github.com/example/trailing-slash-repo/",
                  "commitSha": "abcdef0123456789abcdef0123456789abcdef01",
                  "license": "MIT",
                  "ddlPaths": ["db/schema/**/*.sql"]
                }
              ]
            }
            """);
        var clonesRoot = Path.Combine(Path.GetTempPath(), $"silentscan-verify-corpus-clones-root-{Guid.NewGuid():N}");
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exitCode = await VerifyCorpusCommand.RunAsync(
                new VerifyCorpusCommand.VerifyCorpusOptions(manifestPath, clonesRoot, RepoFilter: null, "high"),
                SqlServerOptions.LocalDocker, stdout, stderr, CancellationToken.None);

            Assert.Equal(1, exitCode);
            var expectedRepoRoot = Path.Combine(clonesRoot, "trailing-slash-repo");
            Assert.Contains($"no local clone at {expectedRepoRoot}", stderr.ToString());
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }
}
