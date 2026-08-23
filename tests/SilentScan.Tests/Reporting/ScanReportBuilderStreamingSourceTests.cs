using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Tests.Support;
using SilentScan.Verify;
using SilentScan.Verify.Catalog;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Reporting;

[Trait("Category", "Oracle")]
public sealed class ScanReportBuilderStreamingSourceTests
{
    [Fact]
    public async Task AllParseResults_IsEnumeratedExactlyOnce()
    {
        const string Sql = """
            CREATE TABLE dbo.Orders (OrderCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            GO
            CREATE VIEW dbo.vw_Orders AS SELECT OrderCode FROM dbo.Orders;
            GO
            CREATE PROCEDURE dbo.usp_Find @OrderCode NVARCHAR(20)
            AS
            BEGIN
                SELECT OrderCode FROM dbo.vw_Orders WHERE OrderCode = @OrderCode;
            END
            """;

        var catalog = await DeployAndReadCatalogAsync(Sql);
        var parseResult = SqlScriptParser.ParseText("source.sql", Sql);
        var countingSource = new EnumerationCountingSource([parseResult]);

        var report = ScanReportBuilder.BuildFromParseResults(countingSource, catalog);

        Assert.Equal(1, countingSource.EnumerationCount);

        var finding = Assert.Single(report.TypedFindings);
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);
    }

    private sealed class EnumerationCountingSource(IReadOnlyList<SqlParseResult> items) : IEnumerable<SqlParseResult>
    {
        public int EnumerationCount { get; private set; }

        public IEnumerator<SqlParseResult> GetEnumerator()
        {
            EnumerationCount++;
            return items.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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
