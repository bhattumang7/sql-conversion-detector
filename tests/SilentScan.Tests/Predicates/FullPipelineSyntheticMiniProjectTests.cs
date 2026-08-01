using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Phase 3 exit criterion (plan.md): "full pipeline on a synthetic mini-project reproduces
/// every finding we planted, zero false fires on the clean twin fixtures." The mini-project
/// lives under fixtures/mini_project/ (schema, views, procs across 3 files, mirroring a real
/// project layout) and is intentionally synthetic per the plan's own wording - distinct from
/// the tier1/ corpus fixtures, which are real-world-sourced per CLAUDE.md's separate rule.
/// Runs through <see cref="ScanReportBuilder"/> (not the individual scanners directly) so the
/// dynamic SQL Tier A pass (reparse + remap) is exercised end to end, same as production.
/// </summary>
public sealed class FullPipelineSyntheticMiniProjectTests
{
    private readonly string _fixtureFile;
    private readonly ScanReport _report;

    public FullPipelineSyntheticMiniProjectTests()
    {
        var projectDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "mini_project");
        var files = SqlFileDiscovery.EnumerateSqlFiles(projectDir);
        _fixtureFile = files.Single(f => f.EndsWith("03_procs.sql", StringComparison.Ordinal));
        _report = ScanReportBuilder.Build(files);

        foreach (var fileHealth in _report.ParseHealth.Files)
        {
            Assert.Empty(fileHealth.Errors);
        }
    }

    [Fact]
    public void DirectTableScanForced_IsPlantedAndFound()
    {
        var finding = Assert.Single(_report.TypedFindings, f => f.Column.ColumnName == "DisplayName" && f.Column.TableQualifiedName == "dbo.Users");

        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal(0, finding.Column.Depth);
        Assert.True(finding.Column.Indexed);
        Assert.Null(finding.DynamicSqlCallSite);
    }

    [Fact]
    public void WindowsCollationRangeSeek_IsPlantedAndFound()
    {
        var finding = Assert.Single(_report.TypedFindings, f => f.Column.ColumnName == "Region");

        Assert.Equal(Verdict.RangeSeek, finding.Verdict);
        Assert.False(finding.Column.Indexed);
    }

    [Fact]
    public void DepthTwoThroughViewChain_IsPlantedAndFound()
    {
        var finding = Assert.Single(_report.TypedFindings, f => f.Column.ColumnName == "OrderCode");

        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal(2, finding.Column.Depth);
        Assert.True(finding.Column.Indexed);
    }

    [Fact]
    public void CleanTwin_SameParamFamilyAndCollation_ProducesNoActionableFinding()
    {
        // usp_FindUserByName_Clean's VARCHAR param against Users.DisplayName - same family
        // and collation, so no actionable verdict should exist for it anywhere in the batch.
        // Only one actionable DisplayName finding total (the planted NVARCHAR one).
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
        // local straight-line DECLARE), so Tier C correctly can't fold it either.
        var finding = Assert.Single(_report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);

        Assert.Equal("undeclared-variable", finding.Reason);
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
    public void DynamicSqlTierCAccumulated_IsPlantedAndFound()
    {
        // usp_DynamicTierCAccumulated_Fires builds its EXEC text via a straight-line
        // DECLARE + SET accumulation across two source lines with no branch in between -
        // Tier C must fold it, reparse it, and remap the resulting ScanForced finding back to
        // the SET statement (line 90) that actually contributed the offending predicate, not
        // the EXEC call site (line 91).
        var finding = Assert.Single(_report.TypedFindings, f => f.Column.ColumnName == "AccountCode");

        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
        Assert.Equal(_fixtureFile, finding.SourcePath);
        Assert.Equal(90, finding.Line);
        Assert.NotNull(finding.DynamicSqlCallSite);
        Assert.Equal(91, finding.DynamicSqlCallSite!.Value.Line);
    }

    [Fact]
    public void DynamicSqlSpExecuteSqlDeclaredParam_TierB_IsPlantedAndFound()
    {
        // sp_executesql's own params declaration string ("N'@Phone nvarchar(20)'") is exact
        // type info - Phone is VARCHAR/SQL_* vs a declared nvarchar param, so this must
        // resolve to ScanForced (Tier B), not Unknown, and carry dynamic SQL call-site
        // provenance the same way Tier A literal findings do.
        var finding = Assert.Single(_report.TypedFindings, f => f.Column.ColumnName == "Phone");

        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
        Assert.NotNull(finding.DynamicSqlCallSite);
        Assert.Equal(_fixtureFile, finding.DynamicSqlCallSite!.Value.SourcePath);
    }

    [Fact]
    public void DynamicSqlLiteral_InnerPredicateIsReparsedAndRemappedToSourceLine()
    {
        // EXEC('SELECT UserId FROM dbo.Users WHERE Email = N''x''') on line 69 of
        // 03_procs.sql - Email is VARCHAR/SQL_* collation vs an nvarchar literal, so Tier A
        // must actually reparse the folded text (not just detect the call site) and remap the
        // resulting ScanForced finding back to that exact source line, with call-site
        // provenance attached.
        var finding = Assert.Single(_report.TypedFindings, f => f.Column.ColumnName == "Email");

        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
        Assert.Equal(_fixtureFile, finding.SourcePath);
        Assert.Equal(69, finding.Line);
        Assert.NotNull(finding.DynamicSqlCallSite);
        Assert.Equal(_fixtureFile, finding.DynamicSqlCallSite!.Value.SourcePath);
        Assert.Equal(69, finding.DynamicSqlCallSite.Value.Line);
    }
}
