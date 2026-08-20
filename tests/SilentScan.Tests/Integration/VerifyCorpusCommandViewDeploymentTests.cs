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
        // usp_FindOrderByCode's own predicate (OrderCode - varchar - compared against an
        // nvarchar literal) is a genuine depth-1 ScanForced finding routed straight through
        // dbo.vw_Orders down to dbo.Orders.OrderCode - CorpusFindingVerifier's own real oracle
        // probe for it has to issue a live query against dbo.vw_Orders itself. If the view had
        // silently failed to deploy (ScriptDeployer.CollectWhitelistedBatches's own empty-batch
        // skip path), that probe could only fail (ProbeFailed) or never run (NotProbeable) -
        // never Confirmed/NotConfirmed - so seeing either of those in the output is a real,
        // positive proof the view landed, not merely the absence of an error string.
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

        // A lineage parity mismatch would mean the view deployed with the wrong shape. Note the
        // bare filename "vw_orders.sql" is no longer a valid absence check on its own - it now
        // legitimately appears as the SourcePath of usp_FindOrderByCode's own successful finding
        // below, so a coarse "this string never appears" assertion would misfire on success.
        Assert.DoesNotContain("LineageParityMismatches\":[{", output.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(0, exitCode);

        // Positive proof the view actually deployed and was actually queried by a real oracle
        // probe (not merely "no failure text appeared", which a silently-skipped view would also
        // satisfy): usp_FindOrderByCode's own ScanForced finding through dbo.vw_Orders must have
        // been probed to completion, landing in Confirmed or NotConfirmed - never ProbeFailed/
        // NotProbeable, which is exactly what a missing view would force instead.
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
