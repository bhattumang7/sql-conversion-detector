using SilentScan.Live;
using SilentScan.Live.Catalog;
using SilentScan.Tests.Support;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

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

        const string adHocQueries = """
            DECLARE @p1 NVARCHAR(30) = N'ABC123';
            SELECT AccountId FROM dbo.Accounts WHERE Code = @p1;
            GO
            DECLARE @p2 NVARCHAR(30) = N'DEF456';
            SELECT AccountId FROM dbo.Accounts WHERE Code = @p2;
            """;
        await new ScriptDeployer(Options).DeployAsync(adHocQueries, DatabaseName);

        var result = await LiveScanRunner.RunAsync(Options.BuildConnectionString(DatabaseName), includePlanCacheEvidence: true);

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
