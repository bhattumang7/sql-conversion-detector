using SilentScan.Core.Rules;
using SilentScan.Live;
using SilentScan.Verify.Catalog;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class LiveScanRunnerTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(LiveScanRunnerTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Orders (
            OrderId INT NOT NULL PRIMARY KEY,
            OrderCode varchar(30) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            INDEX IX_OrderCode (OrderCode));
        GO
        CREATE VIEW dbo.vOrders AS SELECT OrderId, OrderCode FROM dbo.Orders;
        GO
        CREATE PROCEDURE dbo.usp_FindOrder @OrderCode NVARCHAR(30)
        AS
        BEGIN
            SELECT OrderId FROM dbo.vOrders WHERE OrderCode = @OrderCode;
        END
        """;

    [Fact]
    public async Task RunAsync_PredicateThroughViewInsideProcedure_ResolvesToBaseColumn_OracleConfirmed()
    {
        var result = await LiveScanRunner.RunAsync(Options.BuildConnectionString(DatabaseName));

        Assert.Equal(2, result.ModulesAnalyzed);
        Assert.Empty(result.LineageParity.Mismatches);

        var finding = Assert.Single(result.Report.TypedFindings, f => f.Column.ColumnName == "OrderCode");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);
        Assert.True(finding.Column.Indexed);
        Assert.Equal(1, finding.Column.Depth);
        Assert.Equal("dbo.usp_FindOrder", finding.SourcePath);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task RunAsync_CatalogSummary_ReportsRealTableAndModuleCounts()
    {
        var result = await LiveScanRunner.RunAsync(Options.BuildConnectionString(DatabaseName));

        Assert.Equal(1, result.CatalogSummary.TableCount);
        Assert.Equal(2, result.ModulesAnalyzed);
        Assert.Empty(result.CatalogSummary.SkippedConstructs);
    }

    [Fact]
    public async Task RunAsync_EncryptedProcedure_IsReportedUnanalyzableNotSilentlyDropped()
    {

        const string encryptedProcSql = """
            CREATE PROCEDURE dbo.usp_EncryptedLookup WITH ENCRYPTION AS
                SELECT OrderId FROM dbo.Orders WHERE OrderCode = 'x';
            """;
        await new SilentScan.Verify.Deployment.ScriptDeployer(Options).DeployAsync(encryptedProcSql, DatabaseName);

        var result = await LiveScanRunner.RunAsync(Options.BuildConnectionString(DatabaseName));

        Assert.Equal(2, result.ModulesAnalyzed);

        var unanalyzable = Assert.Single(result.UnanalyzableModules, m => m.ObjectName == "usp_EncryptedLookup");
        Assert.Equal(UnanalyzableModuleReason.Encrypted, unanalyzable.Reason);
        Assert.Equal("dbo.usp_EncryptedLookup", unanalyzable.QualifiedName);
    }

    [Fact]
    public async Task RunAsync_NumberedProcedureBodyBeyondFirst_IsReportedUnanalyzableNotSilentlyDropped()
    {

        const string numberedProcSql = """
            CREATE PROCEDURE dbo.usp_Numbered;1 AS SELECT OrderId FROM dbo.Orders WHERE OrderCode = 'x';
            GO
            CREATE PROCEDURE dbo.usp_Numbered;2 AS SELECT OrderId FROM dbo.Orders WHERE OrderCode = 'y';
            """;
        await new SilentScan.Verify.Deployment.ScriptDeployer(Options).DeployAsync(numberedProcSql, DatabaseName);

        var result = await LiveScanRunner.RunAsync(Options.BuildConnectionString(DatabaseName));

        Assert.Equal(3, result.ModulesAnalyzed);

        var unanalyzable = Assert.Single(result.UnanalyzableModules, m => m.ObjectName.StartsWith("usp_Numbered;", StringComparison.Ordinal));
        Assert.Equal(UnanalyzableModuleReason.NumberedProcedureBody, unanalyzable.Reason);
        Assert.Equal("dbo.usp_Numbered;2", unanalyzable.QualifiedName);
    }
}
