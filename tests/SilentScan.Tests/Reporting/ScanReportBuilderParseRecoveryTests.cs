using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Live.Catalog;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Reporting;

/// <summary>
/// docs/audit-remediation-plan.md Phase 4.4, audit finding B4: a file with one bad batch
/// previously vanished from catalog/lineage/predicates entirely, because
/// <see cref="ScanReportBuilder.BuildFromParseResults"/> excluded any parse result with a
/// non-empty Errors list, even though ScriptDOM itself had already dropped only the one
/// malformed batch and kept parsing the rest. "Done when: a file with one bad batch still
/// contributes its other batches' tables."
///
/// This is pure ScriptDOM-level parse recovery - <see cref="SqlScriptParser.ParseText"/> itself
/// drops only the malformed batch and keeps parsing the rest of the file, and
/// <see cref="ScanReportBuilder.BuildFromParseResults"/> must not throw the whole file's
/// <see cref="SqlParseResult"/> away just because its Errors list is non-empty. Neither of those
/// steps touches a database, so this does not route the malformed batch itself through
/// <c>ScriptDeployer.DeployAsync</c> - that deployer executes each GO-separated batch directly
/// against a real SQL Server with no per-batch try/catch (unlike the corpus-facing
/// <c>DeployWhitelistedDdlAsync</c>), so a genuinely malformed batch would throw and abort the
/// whole deployment, taking every later batch's table with it - which is exactly the failure
/// mode this test exists to rule out at the ScanReportBuilder layer, and would make the test
/// depend on deployment behavior it isn't about. Instead: the mixed-batch text is parsed
/// directly (matching the original, pre-engine-authoritative test's own parse step) to prove
/// ScriptDOM's recovery and BuildFromParseResults' resilience, while the catalog it's checked
/// against is read from a real database that was deployed only the SQL known to be valid - the
/// engine is still the sole source of catalog truth, it just never sees the broken batch text.
/// </summary>
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

        // The proc's own WHERE predicate against dbo.Orders is only classifiable if the table's
        // CREATE TABLE batch (unrelated to, and positioned around, the broken batch) still made
        // it into the catalog.
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

    /// <summary>
    /// Deploys only the known-valid SQL text to a fresh disposable database and reads its
    /// catalog back via the engine - no parse-error text ever reaches deployment, since
    /// <see cref="ScriptDeployer.DeployAsync"/> aborts the whole deployment on the first batch
    /// that fails, which a malformed batch always would.
    /// </summary>
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
