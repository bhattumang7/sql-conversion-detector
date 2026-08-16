using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Readable;
using SilentScan.Live;
using SilentScan.Live.Catalog;
using SilentScan.Verify.Catalog;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Reporting;

/// <summary>
/// Live mode's readable report. The findings are laid out exactly as a file scan's are; what
/// this covers is the part only live mode has - the trust signals. A lineage parity mismatch
/// means this tool inferred a view column's type wrongly against the server's own metadata
/// (CLAUDE.md calls that a P0), so it cannot be a line buried under the findings it undermines,
/// and the connection string must never reach a report that gets written to a file and shared.
/// </summary>
public sealed class ReadableLiveScanWriterTests
{
    private static readonly LiveCatalogSummary Catalog = new("SQL_Latin1_General_CP1_CI_AS", 12, 96, 20, 1, []);

    private static Task<ScanReport> EmptyReport() =>
        EngineAuthoritativeScan.ScanAsync("CREATE VIEW dbo.vw_X AS SELECT 1 AS N;");

    private static async Task<LiveScanResult> Result(
        IReadOnlyList<LiveLineageParityMismatch>? mismatches = null,
        IReadOnlyList<UnanalyzableModule>? unanalyzable = null,
        IReadOnlyList<WorkloadFinding>? workloadFindings = null) =>
        new(await EmptyReport(), Catalog, ModulesAnalyzed: 7,
            new LiveLineageParityReport(mismatches ?? [], [], [], []),
            unanalyzable ?? [], PlanCacheEvidence: null, RankedFindings: [], workloadFindings ?? [],
            TempTableExecShapeReport.Empty);

    [Fact]
    public async Task CatalogSummary_SaysWhatWasReadAndThatNothingWasExecuted()
    {
        var rendered = ReadableLiveScanWriter.Write(await Result(), "srv/shop", ReadableStyle.Text);

        Assert.Contains("SilentScan live scan - srv/shop", rendered, StringComparison.Ordinal);
        Assert.Contains("nothing in the target database was executed", rendered, StringComparison.Ordinal);
        Assert.Contains("SQL_Latin1_General_CP1_CI_AS", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LineageParityMismatch_IsReportedAboveTheFindingsItUndermines()
    {
        var rendered = ReadableLiveScanWriter.Write(
            await Result([new LiveLineageParityMismatch("dbo.vw_Orders", "OrderCode", "type", "varchar(20)", "nvarchar(20)")]),
            "srv/shop",
            ReadableStyle.Text);

        Assert.Contains("Column types this tool got wrong (1)", rendered, StringComparison.Ordinal);
        Assert.Contains("dbo.vw_Orders.OrderCode", rendered, StringComparison.Ordinal);
        Assert.Contains("a genuine inference bug in this tool", rendered, StringComparison.Ordinal);
        Assert.True(
            rendered.IndexOf("Column types this tool got wrong", StringComparison.Ordinal) < rendered.IndexOf("Summary", StringComparison.Ordinal),
            "the parity warning must come before the findings it casts doubt on");
    }

    [Fact]
    public async Task UnanalyzableModules_AreNamedRatherThanDropped()
    {
        var rendered = ReadableLiveScanWriter.Write(
            await Result(unanalyzable: [
                new UnanalyzableModule("dbo", "usp_Secret", "P", UnanalyzableModuleReason.Encrypted),
                new UnanalyzableModule("dbo", "fn_Clr", "FS", UnanalyzableModuleReason.ClrAssemblyModule),
            ]),
            "srv/shop",
            ReadableStyle.Text,
            ReadableVerbosity.Full);

        Assert.Contains("Modules with no readable T-SQL body (2)", rendered, StringComparison.Ordinal);
        Assert.Contains("encrypted (WITH ENCRYPTION)", rendered, StringComparison.Ordinal);
        Assert.Contains("backed by a CLR assembly", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultVerbosity_IsBrief_UnanalyzableModulesStateCountWithoutPerModuleDetail()
    {
        var rendered = ReadableLiveScanWriter.Write(
            await Result(unanalyzable: [
                new UnanalyzableModule("dbo", "usp_Secret", "P", UnanalyzableModuleReason.Encrypted),
                new UnanalyzableModule("dbo", "fn_Clr", "FS", UnanalyzableModuleReason.ClrAssemblyModule),
            ]),
            "srv/shop",
            ReadableStyle.Text);

        Assert.Contains("Modules with no readable T-SQL body (2)", rendered, StringComparison.Ordinal);
        Assert.Contains("re-run with --verbosity full", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("usp_Secret", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("backed by a CLR assembly", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultVerbosity_IsBrief_ButNeverGatesTheLineageParityBug()
    {
        var rendered = ReadableLiveScanWriter.Write(
            await Result([new LiveLineageParityMismatch("dbo.vw_Orders", "OrderCode", "type", "varchar(20)", "nvarchar(20)")]),
            "srv/shop",
            ReadableStyle.Text);

        // A genuine inference bug in THIS tool, not a coverage caveat about the scanned
        // database - never gated by verbosity, in either mode.
        Assert.Contains("Column types this tool got wrong (1)", rendered, StringComparison.Ordinal);
        Assert.Contains("dbo.vw_Orders.OrderCode", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Server=srv;Database=shop;User Id=sa;Password=hunter2", "srv/shop")]
    [InlineData("Server=srv;User Id=sa;Password=hunter2", "srv/(default database)")]
    public void DescribeTarget_NamesTheServerAndDatabaseAndNothingElse(string connectionString, string expected)
    {
        var label = ReadableLiveScanWriter.DescribeTarget(connectionString);

        Assert.Equal(expected, label);
        Assert.DoesNotContain("hunter2", label, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeTarget_UnparseableConnectionString_FallsBackRatherThanEchoingIt()
    {
        var label = ReadableLiveScanWriter.DescribeTarget("this is not a connection string===;;");

        Assert.Equal("the connected database", label);
    }
}
