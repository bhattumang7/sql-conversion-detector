using SilentScan.Core.Rules;
using SilentScan.Live;
using SilentScan.Tests.Support;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

/// <summary>
/// Proves the plan-cache ranking signal reflects what the engine is ACTUALLY doing, not just
/// what a static finding claims is possible: deploys a procedure with a real varchar/nvarchar
/// mismatch, executes it several times (populating the real plan cache), then asks
/// <see cref="LiveScanRunner"/> (with plan-cache evidence turned on) whether the resulting
/// finding is observed converting live, with the right execution count.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class LivePlanCacheRankingTests : OracleTestFixture
{
    protected override string DatabaseName => nameof(LivePlanCacheRankingTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Orders (
            OrderId INT NOT NULL PRIMARY KEY,
            OrderCode varchar(30) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            INDEX IX_OrderCode (OrderCode));
        GO
        CREATE PROCEDURE dbo.usp_FindOrder @OrderCode NVARCHAR(30)
        AS
        BEGIN
            SELECT OrderId FROM dbo.Orders WHERE OrderCode = @OrderCode;
        END
        """;

    [Fact]
    public async Task RunAsync_WithPlanCacheEvidence_ObservesRealExecutionsOfAConvertingPredicate()
    {
        // Self-authored setup executing our own deployed proc (never corpus code) to populate
        // the real plan cache with a known execution count to assert against.
        const string executions = """
            EXEC dbo.usp_FindOrder @OrderCode = N'ABC123';
            EXEC dbo.usp_FindOrder @OrderCode = N'DEF456';
            EXEC dbo.usp_FindOrder @OrderCode = N'GHI789';
            """;
        await new ScriptDeployer(Options).DeployAsync(executions, DatabaseName);

        var result = await LiveScanRunner.RunAsync(Options.BuildConnectionString(DatabaseName), includePlanCacheEvidence: true);

        Assert.Null(result.PlanCacheEvidence!.UnavailableReason);

        var ranked = Assert.Single(result.RankedFindings, r => r.Finding.Column.ColumnName == "OrderCode");
        Assert.Equal(Verdict.ScanForced, ranked.Finding.Verdict);
        Assert.True(ranked.ObservedInLivePlanCache);
        Assert.True(ranked.ObservedExecutionCount >= 3, $"expected at least 3 observed executions, saw {ranked.ObservedExecutionCount}");
    }

    [Fact]
    public async Task RunAsync_WithoutPlanCacheEvidence_LeavesRankedFindingsEmpty()
    {
        var result = await LiveScanRunner.RunAsync(Options.BuildConnectionString(DatabaseName));

        Assert.Null(result.PlanCacheEvidence);
        Assert.Empty(result.RankedFindings);
    }
}
