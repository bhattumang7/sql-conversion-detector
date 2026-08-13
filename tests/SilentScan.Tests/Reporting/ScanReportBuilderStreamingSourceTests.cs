using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Tests.Support;
using SilentScan.Verify;
using SilentScan.Verify.Catalog;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Reporting;

/// <summary>
/// A live scan runs against ASTs that are ~200x the size of the module text they parsed from
/// (measured directly: 12MB of module text peaked at 2.5GB RSS) - <c>SilentScan.Live.LiveScanRunner</c>
/// no longer parses every module once and holds a single <c>List&lt;SqlParseResult&gt;</c> for
/// the whole run; instead it keeps only the cheap module TEXT and hands
/// <see cref="ScanReportBuilder.BuildFromParseResults"/> a lazy <c>IEnumerable&lt;SqlParseResult&gt;</c>
/// that reparses fresh every time something enumerates it, so each phase's ASTs become garbage
/// before the next phase's reparse begins.
///
/// That whole design rests on <see cref="ScanReportBuilder.BuildFromParseResults"/> genuinely
/// RE-ENUMERATING its <c>allParseResults</c> parameter multiple times internally (once per
/// full-corpus phase: lineage, call graph, the dynamic-SQL fixpoint rounds, SELECT INTO,
/// Tier-1, typed extraction) rather than materializing it into a list at the top and reading
/// from that materialized copy for the rest of the method - a single `.ToList()` slipped in at
/// the top would silently erase the whole memory win with no functional test able to see it,
/// since every finding would still come out identical. This locks in that contract directly by
/// counting how many times a wrapped source is enumerated.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class ScanReportBuilderStreamingSourceTests
{
    [Fact]
    public async Task AllParseResults_IsEnumeratedMoreThanOnce()
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

        // The exact count is an implementation detail (it depends on how many full-corpus
        // phases the method has); what must never regress is "more than once" - proof that
        // BuildFromParseResults treats its parameter as a genuinely re-enumerable, non-cached
        // SOURCE rather than a one-shot sequence it materializes and reuses internally.
        Assert.True(
            countingSource.EnumerationCount > 1,
            $"expected allParseResults to be enumerated more than once (streaming contract), but it was enumerated {countingSource.EnumerationCount} time(s) - a `.ToList()`/`.ToArray()` was likely added at the top of BuildFromParseResults, silently erasing the memory win of a lazy, reparsing live-mode source.");

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
