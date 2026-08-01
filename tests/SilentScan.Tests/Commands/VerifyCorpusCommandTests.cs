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
            "/no/such/manifest.json", "corpus/_clones", repoFilter: null, SqlServerOptions.LocalDocker, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("manifest not found", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_RepoFilterMatchesNoManifestEntry_ReturnsOneAndWritesError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await VerifyCorpusCommand.RunAsync(
            _manifestPath, "corpus/_clones", repoFilter: "no-such-repo", SqlServerOptions.LocalDocker, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("no manifest entry matches", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_RepoHasNoLocalClone_ReturnsOneAndWritesWarning()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await VerifyCorpusCommand.RunAsync(
            _manifestPath, "/no/such/clones-root", repoFilter: null, SqlServerOptions.LocalDocker, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("no local clone at", stderr.ToString());
    }
}
