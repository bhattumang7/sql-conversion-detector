using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Phase 3 exit criterion (plan.md): "full pipeline on a synthetic mini-project reproduces
/// every finding we planted, zero false fires on the clean twin fixtures." The mini-project
/// lives under fixtures/mini_project/ (schema, views, procs across 3 files, mirroring a real
/// project layout) and is intentionally synthetic per the plan's own wording - distinct from
/// the tier1/ corpus fixtures, which are real-world-sourced per CLAUDE.md's separate rule.
/// Runs through <see cref="ScanReportBuilder"/> (not the individual scanners directly) so the
/// dynamic SQL Tier A pass (reparse + remap) is exercised end to end, same as production - and
/// every planted verdict-bearing finding is additionally confirmed against the real oracle
/// (CLAUDE.md: verify the real thing), deployed from the mini-project's own schema/view DDL
/// (01_schema.sql, 02_views.sql - the procs in 03_procs.sql are never deployed, since the
/// oracle probes reconstruct their own minimal SELECTs against the tables/views directly rather
/// than calling the procs, per CorpusFindingProbeBuilder).
/// </summary>
[Trait("Category", "Oracle")]
public sealed class FullPipelineSyntheticMiniProjectTests : OracleTestFixture
{
    private static readonly string ProjectDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "mini_project");

    private ScanReport _report = null!;

    protected override string DatabaseNameSeed => nameof(FullPipelineSyntheticMiniProjectTests);

    protected override string Ddl =>
        File.ReadAllText(Path.Combine(ProjectDir, "01_schema.sql")) + "\nGO\n" +
        File.ReadAllText(Path.Combine(ProjectDir, "02_views.sql"));

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var files = SqlFileDiscovery.EnumerateSqlFiles(ProjectDir);
        _report = await EngineAuthoritativeScan.ScanFilesAsync(files, "SQL_Latin1_General_CP1_CI_AS");

        foreach (var fileHealth in _report.ParseHealth.Files)
        {
            Assert.Empty(fileHealth.Errors);
        }
    }

    [Fact]
    public async Task DirectTableScanForced_IsPlantedAndFound_OracleConfirmed()
    {
        var finding = Assert.Single(_report.TypedFindings, f => f.Column.ColumnName == "DisplayName" && f.Column.TableQualifiedName == "dbo.Users");

        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal(0, finding.Column.Depth);
        Assert.True(finding.Column.Indexed);
        Assert.Null(finding.DynamicSqlCallSite);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task WindowsCollationRangeSeek_IsPlantedAndFound_OracleConfirmed()
    {
        var finding = Assert.Single(_report.TypedFindings, f => f.Column.ColumnName == "Region");

        Assert.Equal(Verdict.RangeSeek, finding.Verdict);
        Assert.False(finding.Column.Indexed);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task DepthTwoThroughViewChain_IsPlantedAndFound_OracleConfirmed()
    {
        var finding = Assert.Single(_report.TypedFindings, f => f.Column.ColumnName == "OrderCode");

        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal(2, finding.Column.Depth);
        Assert.True(finding.Column.Indexed);

        // The tool's core differentiator - a predicate written two view layers away from the
        // base column still resolves to a real seek-losing conversion on the base table. Probed
        // through the view it was actually written against (vw_OrdersLevel2), per
        // CorpusFindingProbeBuilder's ImmediateRelationQualifiedName use - the optimizer inlines
        // the view either way, so the plan-level CONVERT_IMPLICIT still lands on dbo.Orders.
        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public void CleanTwin_SameParamFamilyAndCollation_ProducesNoActionableFinding()
    {
        // usp_FindUserByName_Clean's VARCHAR param against Users.DisplayName - same family
        // and collation, so no actionable verdict should exist for it anywhere in the batch.
        // Only one actionable DisplayName finding total (the planted NVARCHAR one). SeekPreserved
        // isn't one of the three verdict-bearing enum values PipelineOracleVerification exists
        // for, and this test makes no Verdict claim of its own - it's a count assertion, so
        // there is nothing to add an oracle round-trip for beyond the direct-table test above.
        Assert.Single(_report.TypedFindings, f => f.Column.ColumnName == "DisplayName");
    }

    [Fact]
    public void TypedPredicateSummary_ReflectsEveryClassifiedComparisonNotJustSurvivors()
    {
        // The clean twin above proves at least one SeekPreserved comparison was classified
        // and then dropped from TypedFindings - the summary must still count it, or the
        // report's only base-rate denominator silently loses exactly the comparisons that
        // prove the classifier isn't just flagging everything.
        var summary = _report.TypedPredicateSummary;

        Assert.True(summary.SeekPreservedCount > 0);
        Assert.Equal(
            summary.SeekPreservedCount + summary.RangeSeekCount + summary.ScanForcedCount + summary.UnknownCount,
            summary.TotalClassified);
        Assert.Equal(summary.RangeSeekCount + summary.ScanForcedCount + summary.UnknownCount, _report.TypedFindings.Count);
    }

    [Fact]
    public void Tier1FunctionWrappedColumn_IsPlantedAndFound()
    {
        var finding = Assert.Single(_report.Tier1Findings);

        Assert.Equal(SargabilityFindingKind.FunctionWrappedColumn, finding.Kind);
        Assert.Equal("CreatedAt", finding.ColumnName);
        Assert.Equal("YEAR", finding.Detail);

        // dbo.Users.CreatedAt carries no index in the fixture's schema - the Tier-1 pass must
        // resolve that through the catalog now, not report an unknowable Indexed=null the way
        // it did before catalog/lineage were wired in.
        Assert.Equal("dbo.Users", finding.TableQualifiedName);
        Assert.False(finding.Indexed);
    }

    [Fact]
    public void Tier1CleanTwin_SargableDateRange_ProducesNoFinding()
    {
        // Exactly one Tier-1 finding total across the whole mini-project (the YEAR() one) -
        // the sargable date-range clean twin must not add a second.
        Assert.Single(_report.Tier1Findings);
    }

    [Fact]
    public void DynamicSqlUnanalyzable_IsPlantedAndFound()
    {
        // usp_DynamicVariable_Fires' @Sql is a PROCEDURE PARAMETER (a runtime input, never a
        // local straight-line DECLARE) with no known caller in this fixture, so Tier C correctly
        // can't fold it either - value-seeding across proc-call edges reports its own honest
        // reason here rather than the generic "undeclared-variable" a caller-blind lookup used
        // to produce (the variable IS declared, as a parameter, there's just no caller to learn
        // its value from).
        var finding = Assert.Single(_report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);

        Assert.Equal("procedure-parameter:no-known-call-site", finding.Reason);
    }

    [Fact]
    public void DynamicSqlLiteralOnly_NoInnerFinding_IsAnalyzedWithNoFurtherFindings()
    {
        // EXEC('SELECT 1') has no predicate to find - proves an empty analysis isn't
        // mistakenly reported as unanalyzable.
        var analyzed = _report.DynamicSqlFindings.Where(f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral).ToList();

        Assert.Equal(4, analyzed.Count);
    }

    [Fact]
    public void DynamicSqlCleanTwin_OrdinaryProcCall_ProducesNoFinding()
    {
        // Exactly five dynamic SQL findings total (four literal/sp_executesql/Tier-C, one
        // variable) - the ordinary EXEC dbo.usp_... proc call in the clean-twin proc must
        // not add a sixth.
        Assert.Equal(5, _report.DynamicSqlFindings.Count);
    }

    [Fact]
    public async Task DynamicSqlTierCAccumulated_IsPlantedAndFound_OracleConfirmed()
    {
        // usp_DynamicTierCAccumulated_Fires builds its EXEC text via a straight-line
        // DECLARE + SET accumulation across two source lines with no branch in between -
        // Tier C must fold it, reparse it, and remap the resulting ScanForced finding back to
        // the SET statement that actually contributed the offending predicate, not the EXEC
        // call site one line later. The module's own qualified name is the finding's source
        // path (engine-authoritative scanning maps a finding back to the deployed module, not
        // the original repo file - CLAUDE.md's file-provenance requirement is a corpus-scanning
        // concern, satisfied instead by CorpusLiveScanRunner's own provenance map), and its line
        // numbers are relative to the module's own definition text (sys.sql_modules preserves
        // the original CREATE PROCEDURE text verbatim, starting fresh at line 1 for each module -
        // not the whole file's absolute line count), verified directly against the real engine.
        var finding = Assert.Single(_report.TypedFindings, f => f.Column.ColumnName == "AccountCode");

        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
        Assert.Equal("dbo.usp_DynamicTierCAccumulated_Fires", finding.SourcePath);
        Assert.Equal(5, finding.Line);
        Assert.NotNull(finding.DynamicSqlCallSite);
        Assert.Equal(6, finding.DynamicSqlCallSite!.Value.Line);

        // The dynamic-SQL provenance is purely a source-location concern - the underlying
        // comparison the oracle probes is the same "AccountCode = <nvarchar literal>" against
        // dbo.Users regardless of whether it was written statically or folded from Tier C.
        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task DynamicSqlSpExecuteSqlDeclaredParam_TierB_IsPlantedAndFound_OracleConfirmed()
    {
        // sp_executesql's own params declaration string ("N'@Phone nvarchar(20)'") is exact
        // type info - Phone is VARCHAR/SQL_* vs a declared nvarchar param, so this must
        // resolve to ScanForced (Tier B), not Unknown, and carry dynamic SQL call-site
        // provenance the same way Tier A literal findings do.
        var finding = Assert.Single(_report.TypedFindings, f => f.Column.ColumnName == "Phone");

        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
        Assert.NotNull(finding.DynamicSqlCallSite);
        Assert.Equal("dbo.usp_DynamicSpExecuteSqlDeclaredParam_Fires", finding.DynamicSqlCallSite!.Value.SourcePath);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task DynamicSqlLiteral_InnerPredicateIsReparsedAndRemappedToSourceLine_OracleConfirmed()
    {
        // EXEC('SELECT UserId FROM dbo.Users WHERE Email = N''x''') inside
        // usp_DynamicLiteralWithFinding_Fires - Email is VARCHAR/SQL_* collation vs an
        // nvarchar literal, so Tier A must actually reparse the folded text (not just detect
        // the call site) and remap the resulting ScanForced finding back to that exact source
        // line within the module's own definition, with call-site provenance attached -
        // verified directly against the real engine (see the sibling TierC test above for why
        // the source path/line are module-relative under engine-authoritative scanning).
        var finding = Assert.Single(_report.TypedFindings, f => f.Column.ColumnName == "Email");

        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
        Assert.Equal("dbo.usp_DynamicLiteralWithFinding_Fires", finding.SourcePath);
        Assert.Equal(4, finding.Line);
        Assert.NotNull(finding.DynamicSqlCallSite);
        Assert.Equal("dbo.usp_DynamicLiteralWithFinding_Fires", finding.DynamicSqlCallSite!.Value.SourcePath);
        Assert.Equal(4, finding.DynamicSqlCallSite.Value.Line);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }
}
