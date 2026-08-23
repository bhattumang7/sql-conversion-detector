using SilentScan.Core.Rules;
using SilentScan.Live;
using SilentScan.Tests.Support;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class LivePlanCacheRankingTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(LivePlanCacheRankingTests);

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
