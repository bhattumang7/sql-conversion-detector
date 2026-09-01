using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Readable;
using SilentScan.Core.Rules;
using SilentScan.Core.TypeInference;
using SilentScan.Live;
using SilentScan.Live.Catalog;
using SilentScan.Verify.Catalog;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Reporting;

[Trait("Category", "Oracle")]
public sealed class ReadableLiveScanWriterTests
{
    private static readonly LiveCatalogSummary Catalog = new("SQL_Latin1_General_CP1_CI_AS", 12, 96, 20, 1, []);

    private static Task<ScanReport> EmptyReport() =>
        EngineAuthoritativeScan.ScanAsync("CREATE VIEW dbo.vw_X AS SELECT 1 AS N;");

    private static async Task<LiveScanResult> Result(
        IReadOnlyList<LiveLineageParityMismatch>? mismatches = null,
        IReadOnlyList<LiveLineageStaleMetadata>? stale = null,
        IReadOnlyList<LiveLineageUncompilableObject>? uncompilable = null,
        IReadOnlyList<LiveLineageUnverifiedColumn>? unverified = null,
        IReadOnlyList<UnanalyzableModule>? unanalyzable = null,
        PlanCacheEvidenceResult? planCacheEvidence = null,
        IReadOnlyList<RankedFinding>? rankedFindings = null,
        IReadOnlyList<WorkloadFinding>? workloadFindings = null) =>
        new(await EmptyReport(), Catalog, ModulesAnalyzed: 7,
            new LiveLineageParityReport(mismatches ?? [], stale ?? [], uncompilable ?? [], unverified ?? []),
            unanalyzable ?? [], planCacheEvidence, rankedFindings ?? [], workloadFindings ?? [],
            TempTableExecShapeReport.Empty, ExecResultSetsShapeReport.Empty);

    private static RankedFinding Finding(string table, string column, string sourcePath, int line, bool observed, long executionCount) =>
        new(
            new TypedPredicateFinding(
                Verdict.ScanForced,
                new PredicateOperand.Column(table, column, new SqlType(SqlTypeCategory.VarChar), Indexed: true, Depth: 0, Provenance: null!),
                new PredicateOperand.Value(null),
                "=",
                sourcePath,
                line,
                1),
            observed,
            executionCount);

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
            ReadableStyle.Text).ReplaceLineEndings("\n");

        Assert.Contains("Column types this tool got wrong (1)", rendered, StringComparison.Ordinal);
        Assert.Contains("dbo.vw_Orders.OrderCode", rendered, StringComparison.Ordinal);
        Assert.Contains("a genuine inference bug in this tool", rendered, StringComparison.Ordinal);

        var summaryHeading = "Summary\n" + new string('-', "Summary".Length);
        Assert.True(
            rendered.IndexOf("Column types this tool got wrong", StringComparison.Ordinal) <
            rendered.IndexOf(summaryHeading, StringComparison.Ordinal),
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

    [Fact]
    public async Task UncompilableObjects_FullVerbosity_ListsErrorNumberAndMessage()
    {
        var rendered = ReadableLiveScanWriter.Write(
            await Result(uncompilable: [new LiveLineageUncompilableObject("dbo.vw_Broken", 208, "Invalid object name.")]),
            "srv/shop",
            ReadableStyle.Text,
            ReadableVerbosity.Full);

        Assert.Contains("Objects the server cannot compile (1)", rendered, StringComparison.Ordinal);
        Assert.Contains("dbo.vw_Broken", rendered, StringComparison.Ordinal);
        Assert.Contains("208", rendered, StringComparison.Ordinal);
        Assert.Contains("Invalid object name.", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UncompilableObjects_BriefVerbosity_ShowsCountWithoutPerObjectDetail()
    {
        var rendered = ReadableLiveScanWriter.Write(
            await Result(uncompilable: [new LiveLineageUncompilableObject("dbo.vw_Broken", 208, "Invalid object name.")]),
            "srv/shop",
            ReadableStyle.Text);

        Assert.Contains("Objects the server cannot compile (1)", rendered, StringComparison.Ordinal);
        Assert.Contains("1 object - not listed individually here", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Invalid object name.", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleCachedMetadata_FullVerbosity_ShowsCachedAndLiveValues()
    {
        var rendered = ReadableLiveScanWriter.Write(
            await Result(stale: [new LiveLineageStaleMetadata("dbo.vw_Aged", "Amount", "type", "decimal(10,2)", "decimal(12,2)")]),
            "srv/shop",
            ReadableStyle.Text,
            ReadableVerbosity.Full);

        Assert.Contains("Objects whose cached metadata is out of date (1)", rendered, StringComparison.Ordinal);
        Assert.Contains("dbo.vw_Aged.Amount", rendered, StringComparison.Ordinal);
        Assert.Contains("decimal(10,2)", rendered, StringComparison.Ordinal);
        Assert.Contains("decimal(12,2)", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleCachedMetadata_BriefVerbosity_ShowsCountWithoutPerColumnDetail()
    {
        var rendered = ReadableLiveScanWriter.Write(
            await Result(stale: [
                new LiveLineageStaleMetadata("dbo.vw_Aged", "Amount", "type", "decimal(10,2)", "decimal(12,2)"),
                new LiveLineageStaleMetadata("dbo.vw_Aged", "Qty", "type", "int", "bigint"),
            ]),
            "srv/shop",
            ReadableStyle.Text);

        Assert.Contains("Objects whose cached metadata is out of date (2)", rendered, StringComparison.Ordinal);
        Assert.Contains("2 objects - not listed individually here", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("decimal(10,2)", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnverifiedColumns_FullVerbosity_ShowsReasonInferredAndCachedValues()
    {
        var rendered = ReadableLiveScanWriter.Write(
            await Result(unverified: [new LiveLineageUnverifiedColumn("dbo.vw_Odd", "Flag", "describe_first_result_set timed out", "bit", "tinyint")]),
            "srv/shop",
            ReadableStyle.Text,
            ReadableVerbosity.Full);

        Assert.Contains("Columns that could not be live-verified (1)", rendered, StringComparison.Ordinal);
        Assert.Contains("dbo.vw_Odd.Flag", rendered, StringComparison.Ordinal);
        Assert.Contains("describe_first_result_set timed out", rendered, StringComparison.Ordinal);
        Assert.Contains("bit", rendered, StringComparison.Ordinal);
        Assert.Contains("tinyint", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnverifiedColumns_BriefVerbosity_ShowsCountWithoutPerColumnDetail()
    {
        var rendered = ReadableLiveScanWriter.Write(
            await Result(unverified: [new LiveLineageUnverifiedColumn("dbo.vw_Odd", "Flag", "describe_first_result_set timed out", "bit", "tinyint")]),
            "srv/shop",
            ReadableStyle.Text);

        Assert.Contains("Columns that could not be live-verified (1)", rendered, StringComparison.Ordinal);
        Assert.Contains("1 column - not listed individually here", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("describe_first_result_set timed out", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanCacheEvidence_Absent_OmitsSectionEntirely()
    {
        var rendered = ReadableLiveScanWriter.Write(await Result(), "srv/shop", ReadableStyle.Text);

        Assert.DoesNotContain("Confirmed by the live plan cache", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanCacheEvidence_Unavailable_ExplainsWhyAndOmitsAnyFindingsTable()
    {
        var rendered = ReadableLiveScanWriter.Write(
            await Result(
                planCacheEvidence: new PlanCacheEvidenceResult([], PlansInspected: 5, UnavailableReason: "query store disabled"),
                rankedFindings: [Finding("syn.OrdersA", "CodeA", "src/orders/predicate.sql", 12, observed: true, executionCount: 3)]),
            "srv/shop",
            ReadableStyle.Text);

        Assert.Contains("Confirmed by the live plan cache", rendered, StringComparison.Ordinal);
        Assert.Contains("The plan cache could not be read (query store disabled)", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("cached plans were inspected", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("CodeA", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanCacheEvidence_NoObservedFindings_SaysNoneConvertInLivePlan()
    {
        var rendered = ReadableLiveScanWriter.Write(
            await Result(
                planCacheEvidence: new PlanCacheEvidenceResult([], PlansInspected: 10, UnavailableReason: null),
                rankedFindings: [Finding("syn.OrdersB", "CodeB", "src/orders/predicate.sql", 4, observed: false, executionCount: 0)]),
            "srv/shop",
            ReadableStyle.Text);

        Assert.Contains("10 cached plans were inspected", rendered, StringComparison.Ordinal);
        Assert.Contains("None of the static findings below show up as an actual conversion", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("CodeB", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanCacheEvidence_ObservedFindings_ExcludesUnobservedAndOrdersByExecutionCountDescending()
    {
        var rendered = ReadableLiveScanWriter.Write(
            await Result(
                planCacheEvidence: new PlanCacheEvidenceResult([], PlansInspected: 3, UnavailableReason: null),
                rankedFindings:
                [
                    Finding("syn.LowVolume", "ColLow", "src/orders/low.sql", 1, observed: true, executionCount: 5),
                    Finding("syn.HighVolume", "ColHigh", "src/orders/high.sql", 1, observed: true, executionCount: 50),
                    Finding("syn.NeverRun", "ColNever", "src/orders/never.sql", 1, observed: false, executionCount: 999),
                ]),
            "srv/shop",
            ReadableStyle.Text);

        Assert.Contains("2 of the findings below are converting in a plan the server is running right now", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("ColNever", rendered, StringComparison.Ordinal);

        var highIndex = rendered.IndexOf("ColHigh", StringComparison.Ordinal);
        var lowIndex = rendered.IndexOf("ColLow", StringComparison.Ordinal);
        Assert.True(highIndex >= 0 && lowIndex >= 0 && highIndex < lowIndex, "the higher-execution-count finding must be listed first");
    }

    [Fact]
    public async Task WorkloadFindings_Empty_OmitsSectionEntirely()
    {
        var rendered = ReadableLiveScanWriter.Write(await Result(), "srv/shop", ReadableStyle.Text);

        Assert.DoesNotContain("Conversions observed in the workload", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkloadFindings_MapVerdictTextAndOrderByExecutionCountDescending()
    {
        var rendered = ReadableLiveScanWriter.Write(
            await Result(workloadFindings:
            [
                new WorkloadFinding("syn.WorkA", "ColA", Indexed: true, WorkloadVerdict.ScanForced, ExecutionCount: 20),
                new WorkloadFinding("syn.WorkB", "ColB", Indexed: false, WorkloadVerdict.RangeSeek, ExecutionCount: 100),
            ]),
            "srv/shop",
            ReadableStyle.Text);

        Assert.Contains("Conversions observed in the workload, not in any scanned module (2)", rendered, StringComparison.Ordinal);
        Assert.Contains("forces a scan", rendered, StringComparison.Ordinal);
        Assert.Contains("degrades the seek", rendered, StringComparison.Ordinal);

        var workBIndex = rendered.IndexOf("ColB", StringComparison.Ordinal);
        var workAIndex = rendered.IndexOf("ColA", StringComparison.Ordinal);
        Assert.True(workBIndex >= 0 && workAIndex >= 0 && workBIndex < workAIndex, "the higher-execution-count finding must be listed first");
    }
}
