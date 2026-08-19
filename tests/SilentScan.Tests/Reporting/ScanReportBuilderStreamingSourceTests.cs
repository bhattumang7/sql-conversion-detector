using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Tests.Support;
using SilentScan.Verify;
using SilentScan.Verify.Catalog;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Reporting;

/// <summary>
/// <see cref="ScanReportBuilder.BuildFromParseResults"/> must consume its <c>allParseResults</c>
/// parameter EXACTLY ONCE, materializing it up front rather than re-enumerating it per phase.
///
/// This test previously asserted the opposite. A live scan hands in a lazy source that reparses
/// every module from cheap retained text on each enumeration, and the intent was that never
/// materializing would keep peak memory down, since a parsed AST runs ~200x the size of its
/// source text. Measured directly, it did not: the method runs ~50 whole-database phases, so the
/// lazy source was re-enumerated 50 times and every module parsed 50 times per scan. At the scale
/// that motivated the design (800 modules, ~12MB of module text) that cost 380s and 87GB of
/// allocation against 27s and 6GB once materialized, while peak working set differed by 5.9%
/// (1,683MB lazy versus 1,783MB materialized). Re-parsing bought a rounding error of memory for a
/// 14x runtime penalty, and the garbage it produced is what made a forced GC at every phase
/// boundary look necessary (see <c>PhaseMemory</c>).
///
/// The contract worth locking in is therefore the inverse one, and it needs a test for the same
/// reason the old one did: every finding comes out identical either way, so nothing functional
/// would notice a regression back to per-phase re-enumeration.
/// </summary>
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

        // Exactly one: the count is not an implementation detail here, because a live source
        // reparses every module on each enumeration. Two enumerations means every module is
        // parsed twice, and the method has ~50 phases to grow that back into a 50x parse.
        Assert.Equal(1, countingSource.EnumerationCount);

        // Not a vacuous pass: the pipeline still produces the real finding through however many
        // re-enumerations happened.
        var finding = Assert.Single(report.TypedFindings);
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);
    }

    /// <summary>Wraps a fixed sequence, counting how many independent enumerations it's ever asked for - never caching, so re-enumerating genuinely re-walks the source.</summary>
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
