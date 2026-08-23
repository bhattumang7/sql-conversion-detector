using SilentScan.Verify;
using SilentScan.Verify.Commands;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class VerifyCorpusCommandDialectSniffingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"silentscan-verify-dialect-test-{Guid.NewGuid():N}");
    private readonly string _manifestPath;

    public VerifyCorpusCommandDialectSniffingTests()
    {
        Directory.CreateDirectory(_root);
        var cloneDir = Path.Combine(_root, "clones", "example");
        Directory.CreateDirectory(cloneDir);

        File.WriteAllText(Path.Combine(cloneDir, "schema.sql"), """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY);
            GO
            """);

        File.WriteAllText(Path.Combine(cloneDir, "not_tsql.sql"), "THIS IS $$$ NOT VALID T-SQL AT ALL ((((");

        _manifestPath = Path.Combine(_root, "manifest.json");
        File.WriteAllText(_manifestPath, """
            {
              "repos": [
                {
                  "name": "dialect-sniff-example",
                  "url": "https://github.com/example/example",
                  "commitSha": "abcdef0123456789abcdef0123456789abcdef01",
                  "license": "MIT",
                  "ddlPaths": ["*.sql"],
                  "procPaths": ["*.sql"],
                  "declaredCollation": null
                }
              ]
            }
            """);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task RunAsync_RepoBelowDialectSniffingThreshold_WarnsAndReturnsOne()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await VerifyCorpusCommand.RunAsync(
            new VerifyCorpusCommand.VerifyCorpusOptions(_manifestPath, Path.Combine(_root, "clones"), RepoFilter: null, "high"),
            SqlServerOptions.LocalDocker, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("dialect-sniffing threshold", stderr.ToString(), StringComparison.Ordinal);

        using var document = System.Text.Json.JsonDocument.Parse(stdout.ToString());
        var summary = document.RootElement.GetProperty("dialect-sniff-example");
        Assert.False(summary.GetProperty("PassesDialectSniffing").GetBoolean());
        Assert.Equal(0.5, summary.GetProperty("ParseSuccessRate").GetDouble());
    }

    [Fact]
    public async Task RunAsync_RepoAtOrAboveDialectSniffingThreshold_NoWarningAndReturnsZero()
    {
        var cloneDir = Path.Combine(_root, "clones", "example");
        File.Delete(Path.Combine(cloneDir, "not_tsql.sql"));

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await VerifyCorpusCommand.RunAsync(
            new VerifyCorpusCommand.VerifyCorpusOptions(_manifestPath, Path.Combine(_root, "clones"), RepoFilter: null, "high"),
            SqlServerOptions.LocalDocker, stdout, stderr, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("dialect-sniffing threshold", stderr.ToString(), StringComparison.Ordinal);

        using var document = System.Text.Json.JsonDocument.Parse(stdout.ToString());
        var summary = document.RootElement.GetProperty("dialect-sniff-example");
        Assert.True(summary.GetProperty("PassesDialectSniffing").GetBoolean());
        Assert.Equal(1.0, summary.GetProperty("ParseSuccessRate").GetDouble());
    }
}
