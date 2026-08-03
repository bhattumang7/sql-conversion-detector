using SilentScan.Core.Rules;
using SilentScan.Live;
using SilentScan.Live.Catalog;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

/// <summary>
/// End-to-end proof that <see cref="LiveScanRunner"/> reproduces the flagship implicit-
/// conversion bug through nothing but a live connection string - no DDL files, no manifest, no
/// pinned --collation: the view/procedure bodies come from <c>sys.sql_modules</c>, the catalog
/// (including the base table's collation and the fact that OrderCode is indexed) comes from
/// engine metadata, and the resulting ScanForced finding is oracle-confirmed against the same
/// real plan XML every other verdict-bearing test in this suite is held to.
/// </summary>
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
        Assert.Empty(result.LineageParityMismatches);

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
        // sys.sql_modules.definition is genuinely NULL for a WITH ENCRYPTION module - there is
        // no T-SQL body to recover from metadata at all (unlike a corpus file this project just
        // chose not to parse). This must surface as an accounted-for gap, never a module that
        // silently vanishes from the module count with no trace.
        const string encryptedProcSql = """
            CREATE PROCEDURE dbo.usp_EncryptedLookup WITH ENCRYPTION AS
                SELECT OrderId FROM dbo.Orders WHERE OrderCode = 'x';
            """;
        await new SilentScan.Verify.Deployment.ScriptDeployer(Options).DeployAsync(encryptedProcSql, DatabaseName);

        var result = await LiveScanRunner.RunAsync(Options.BuildConnectionString(DatabaseName));

        // The two plaintext modules from the class-level Ddl are unaffected.
        Assert.Equal(2, result.ModulesAnalyzed);

        var unanalyzable = Assert.Single(result.UnanalyzableModules, m => m.ObjectName == "usp_EncryptedLookup");
        Assert.Equal(UnanalyzableModuleReason.Encrypted, unanalyzable.Reason);
        Assert.Equal("dbo.usp_EncryptedLookup", unanalyzable.QualifiedName);
    }
}
