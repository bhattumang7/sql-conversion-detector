using SilentScan.Verify;
using SilentScan.Verify.Commands;

namespace SilentScan.Tests.Integration;

/// <summary>
/// End-to-end: a repo whose manifest keeps views in `procPaths` separate from `ddlPaths`
/// (WideWorldImporters' own layout) must still get those views deployed, or a depth&gt;=1
/// finding's probe (which now queries the view - see <see cref="Oracle.CorpusFindingVerifierTests"/>)
/// has nothing to compile against. Exercises `VerifyCorpusCommand.RunAsync`'s real deployment
/// path against the live Docker oracle, not a synthesized scenario.
/// </summary>
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

        // Only reachable from procPaths, never ddlPaths - mirrors WideWorldImporters' own
        // manifest entry, the case that motivated deploying procPaths' view/function DDL at all.
        File.WriteAllText(Path.Combine(cloneDir, "Views", "vw_orders.sql"), """
            CREATE VIEW dbo.vw_Orders AS SELECT OrderId, OrderCode FROM dbo.Orders;
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

        // A lineage parity mismatch (or a deployment error mentioning the view file) would mean
        // the view either didn't deploy or deployed with the wrong shape - the JSON output
        // carries both, so a simple string check here is a real (if coarse) end-to-end signal
        // that the view landed in the disposable database as expected.
        Assert.DoesNotContain("vw_orders.sql", output, StringComparison.Ordinal);
        Assert.DoesNotContain("LineageParityMismatches\":[{", output.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(0, exitCode);
    }
}
