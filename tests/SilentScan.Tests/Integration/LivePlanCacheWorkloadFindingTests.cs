using SilentScan.Live;
using SilentScan.Live.Catalog;
using SilentScan.Tests.Support;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

/// <summary>
/// Roadmap Phase D: proves the actual product pivot, not just the ranking bonus
/// <see cref="LivePlanCacheRankingTests"/> covers - an ad-hoc, parameterized query that was
/// NEVER a stored procedure body at all (the dominant real-world source of implicit conversions:
/// application code, an ORM, a hand-written data-access layer) must still surface as its own
/// finding once the plan cache shows it converting, even though no module-body scan could ever
/// have produced it.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class LivePlanCacheWorkloadFindingTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(LivePlanCacheWorkloadFindingTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Accounts (
            AccountId INT NOT NULL PRIMARY KEY,
            Code varchar(30) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            INDEX IX_Code (Code));
        """;

    [Fact]
    public async Task RunAsync_AdHocParameterizedQueryNeverInAModule_SurfacesAsWorkloadFinding()
    {
        // Self-authored, executed directly against our own disposable database - never a stored
        // procedure, exactly the shape this scan has no module body to find on its own.
        const string adHocQueries = """
            DECLARE @p1 NVARCHAR(30) = N'ABC123';
            SELECT AccountId FROM dbo.Accounts WHERE Code = @p1;
            GO
            DECLARE @p2 NVARCHAR(30) = N'DEF456';
            SELECT AccountId FROM dbo.Accounts WHERE Code = @p2;
            """;
        await new ScriptDeployer(Options).DeployAsync(adHocQueries, DatabaseName);

        var result = await LiveScanRunner.RunAsync(Options.BuildConnectionString(DatabaseName), includePlanCacheEvidence: true);

        // No module body exists for this query at all, so the ordinary static pipeline must
        // have found nothing on dbo.Accounts.Code - proving the workload finding below isn't
        // just duplicating something the module-body pass already caught.
        Assert.DoesNotContain(result.Report.TypedFindings, f => f.Column.ColumnName == "Code");

        var workloadFinding = Assert.Single(result.WorkloadFindings, f => f.ColumnName == "Code");
        Assert.Equal("dbo.Accounts", workloadFinding.TableQualifiedName);
        Assert.True(workloadFinding.Indexed);
        Assert.Equal(WorkloadVerdict.ScanForced, workloadFinding.Verdict);
        Assert.True(workloadFinding.ExecutionCount >= 2, $"expected at least 2 observed executions, saw {workloadFinding.ExecutionCount}");
    }

    [Fact]
    public async Task RunAsync_WithoutPlanCacheEvidence_LeavesWorkloadFindingsEmpty()
    {
        var result = await LiveScanRunner.RunAsync(Options.BuildConnectionString(DatabaseName));

        Assert.Empty(result.WorkloadFindings);
    }
}
