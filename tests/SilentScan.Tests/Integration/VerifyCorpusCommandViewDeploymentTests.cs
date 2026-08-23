using SilentScan.Verify;
using SilentScan.Verify.Commands;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class VerifyCorpusCommandViewDeploymentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"silentscan-verify-view-deploy-test-{Guid.NewGuid():N}");
    private readonly string _manifestPath;

    public VerifyCorpusCommandViewDeploymentTests()
    {
        Directory.CreateDirectory(_root);
        var cloneDir = Path.Combine(_root, "clones", "example");
        Directory.CreateDirectory(Path.Combine(cloneDir, "Tables"));
        Directory.CreateDirectory(Path.Combine(cloneDir, "Views"));

        File.WriteAllText(Path.Combine(cloneDir, "Tables", "orders.sql"), """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, OrderCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            GO
            CREATE INDEX IX_Orders_OrderCode ON dbo.Orders(OrderCode);
            GO
            """);

        File.WriteAllText(Path.Combine(cloneDir, "Views", "vw_orders.sql"), """
            CREATE VIEW dbo.vw_Orders AS SELECT OrderId, OrderCode FROM dbo.Orders;
            GO
            CREATE PROCEDURE dbo.usp_FindOrderByCode AS
            BEGIN
                SELECT OrderId FROM dbo.vw_Orders WHERE OrderCode = N'ABC';
            END;
            GO
            """);

        _manifestPath = Path.Combine(_root, "manifest.json");
        File.WriteAllText(_manifestPath, """
            {
              "repos": [
                {
                  "name": "view-deploy-example",
                  "url": "https://github.com/example/example",
                  "commitSha": "abcdef0123456789abcdef0123456789abcdef01",
                  "license": "MIT",
                  "ddlPaths": ["Tables/*.sql"],
                  "procPaths": ["Views/*.sql"],
                  "declaredCollation": null
                }
              ]
            }
            """);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task RunAsync_ViewOnlyInProcPaths_StillDeploysAndParticipatesInLineageParity()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await VerifyCorpusCommand.RunAsync(
            new VerifyCorpusCommand.VerifyCorpusOptions(_manifestPath, Path.Combine(_root, "clones"), RepoFilter: null, "high"),
            SqlServerOptions.LocalDocker, stdout, stderr, CancellationToken.None);

        var output = stdout.ToString();

        Assert.DoesNotContain("LineageParityMismatches\":[{", output.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(0, exitCode);

        using var document = System.Text.Json.JsonDocument.Parse(output);
        var summary = document.RootElement.GetProperty("view-deploy-example");
        var confirmedCount = summary.GetProperty("Confirmed").GetArrayLength();
        var notConfirmedCount = summary.GetProperty("NotConfirmed").GetArrayLength();
        Assert.Equal(0, summary.GetProperty("ProbeFailed").GetArrayLength());
        Assert.Equal(0, summary.GetProperty("NotProbeable").GetArrayLength());
        Assert.True(
            confirmedCount + notConfirmedCount > 0,
            "expected usp_FindOrderByCode's depth-1 finding through dbo.vw_Orders to have been probed and resolved");
    }
}
