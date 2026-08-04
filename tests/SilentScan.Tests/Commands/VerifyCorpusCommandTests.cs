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
        // verify-corpus's own --confidence must reject the same way scan-db/scan-corpus-live do
        // (FindingConfidenceParsing is the single shared parser both CLIs use) - checked before
        // this command ever deploys anything, so a typo fails fast rather than burning a full
        // Docker provisioning cycle first.
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await VerifyCorpusCommand.RunAsync(
            new VerifyCorpusCommand.VerifyCorpusOptions(_manifestPath, "corpus/_clones", RepoFilter: null, "extreme"),
            SqlServerOptions.LocalDocker, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("unknown --confidence", stderr.ToString());
    }
}
