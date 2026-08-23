using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Verify.Catalog;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Reporting;

[Trait("Category", "Oracle")]
public sealed class ScanReportBuilderParseRecoveryTests
{
    [Fact]
    public async Task FileWithOneBadBatch_OtherBatchesTableStillContributesToCatalog()
    {
        const string ValidSql = """
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_Find @OrderCode NVARCHAR(20)
            AS
            BEGIN
                SELECT OrderCode FROM dbo.Orders WHERE OrderCode = @OrderCode;
            END
            """;

        const string MixedSql = """
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            GO
            CREATE TABLE dbo.Bad ((( THIS IS NOT VALID SYNTAX;
            GO
            CREATE PROCEDURE dbo.usp_Find @OrderCode NVARCHAR(20)
            AS
            BEGIN
                SELECT OrderCode FROM dbo.Orders WHERE OrderCode = @OrderCode;
            END
            GO
            """;

        var result = SqlScriptParser.ParseText("mixed.sql", MixedSql);

        Assert.True(result.HasErrors);
        Assert.Equal(2, result.BatchCount);

        var catalog = await DeployAndReadCatalogAsync(ValidSql);
        var report = ScanReportBuilder.BuildFromParseResults([result], catalog);

        var health = Assert.Single(report.ParseHealth.Files);
        Assert.NotEmpty(health.Errors);
        Assert.Equal(2, health.BatchCount);

        var finding = Assert.Single(report.TypedFindings);
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);
    }

    [Fact]
    public void FileWithNoSurvivingBatches_ContributesNothingButIsStillReportedInParseHealth()
    {
        var result = SqlScriptParser.ParseText("garbage.sql", "SELECT FROM WHERE;;;");

        Assert.True(result.HasErrors);
        Assert.Equal(0, result.BatchCount);

        var report = ScanReportBuilder.BuildFromParseResults([result], new DatabaseCatalog());

        var health = Assert.Single(report.ParseHealth.Files);
        Assert.NotEmpty(health.Errors);
        Assert.Equal(0, health.BatchCount);
        Assert.Empty(report.TypedFindings);
        Assert.Empty(report.Tier1Findings);
    }

    private static async Task<DatabaseCatalog> DeployAndReadCatalogAsync(string sql, CancellationToken cancellationToken = default)
    {
        var options = SqlServerOptions.LocalDocker;
        var databaseName = $"SilentScanTest_{Guid.NewGuid():N}";
        var provisioner = new DatabaseProvisioner(options);
        await provisioner.CreateFreshAsync(databaseName, cancellationToken: cancellationToken);
        try
        {
            await new ScriptDeployer(options).DeployAsync(sql, databaseName, cancellationToken);
            return await new LiveCatalogReader(options.BuildConnectionString(databaseName)).ReadAsync(cancellationToken);
        }
        finally
        {
            await provisioner.DropIfExistsAsync(databaseName, cancellationToken);
        }
    }
}
