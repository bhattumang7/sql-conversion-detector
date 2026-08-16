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

/// <summary>
/// Corrections-to-shipped-work item: a single cached plan carrying two independent implicit
/// conversions - one genuinely range-seeking (Windows collation), one genuinely scan-forced
/// (SQL_* collation) - must not let the range-seek marker "rescue" the scan-forced column's
/// verdict just because both conversions happen to live in the same plan XML. Oracle-verified
/// against the real Docker instance: a two-branch UNION ALL produces one Concatenation plan whose
/// Windows-collation branch shows GetRangeThroughConvert scoped to its own RelOp's SeekPredicates,
/// while the SQL_*-collation branch's RelOp has no SeekPredicates entry for its column at all.
/// Before the fix, LivePlanCacheReader read the marker plan-wide and marked both RangeSeek.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class LivePlanCacheReaderPerConversionAttributionOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(LivePlanCacheReaderPerConversionAttributionOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Probe (
            Id INT NOT NULL PRIMARY KEY,
            WindowsColCol VARCHAR(50) COLLATE Latin1_General_CI_AS NOT NULL,
            SqlColCol VARCHAR(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            INDEX IX_WindowsColCol (WindowsColCol),
            INDEX IX_SqlColCol (SqlColCol));
        """;

    [Fact]
    public async Task RunAsync_OnePlanWithBothRangeSeekAndScanForcedConversions_AttributesEachColumnIndependently()
    {
        // A range seek is a cost-based choice - on a table with no rows the optimizer never
        // considers one regardless of whether GetRangeThroughConvert could apply, so both
        // branches would trivially scan and this test would pass for the wrong reason. Real
        // row volume (oracle-verified: 2 rows was not enough, 2000 was) is what makes the
        // optimizer actually pick "Index Seek" for the Windows-collation branch.
        const string seedRows = """
            INSERT INTO dbo.Probe (Id, WindowsColCol, SqlColCol)
            SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
                   'V' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS VARCHAR(10)),
                   'V' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS VARCHAR(10))
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            UPDATE STATISTICS dbo.Probe WITH FULLSCAN;
            """;
        await new ScriptDeployer(Options).DeployAsync(seedRows, DatabaseName);

        const string adHocQuery = """
            DECLARE @w NVARCHAR(50) = N'V1', @s NVARCHAR(50) = N'V1';
            SELECT Id FROM dbo.Probe WHERE WindowsColCol = @w
            UNION ALL
            SELECT Id FROM dbo.Probe WHERE SqlColCol = @s;
            """;
        await new ScriptDeployer(Options).DeployAsync(adHocQuery, DatabaseName);

        var result = await LiveScanRunner.RunAsync(Options.BuildConnectionString(DatabaseName), includePlanCacheEvidence: true);

        var windowsFinding = Assert.Single(result.WorkloadFindings, f => f.ColumnName == "WindowsColCol");
        Assert.Equal(WorkloadVerdict.RangeSeek, windowsFinding.Verdict);

        var sqlFinding = Assert.Single(result.WorkloadFindings, f => f.ColumnName == "SqlColCol");
        Assert.Equal(WorkloadVerdict.ScanForced, sqlFinding.Verdict);
    }
}
