using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Phase 3 exit criterion (plan.md): "full pipeline on a synthetic mini-project reproduces
/// every finding we planted, zero false fires on the clean twin fixtures." The mini-project
/// lives under fixtures/mini_project/ (schema, views, procs across 3 files, mirroring a real
/// project layout) and is intentionally synthetic per the plan's own wording - distinct from
/// the tier1/ corpus fixtures, which are real-world-sourced per CLAUDE.md's separate rule.
/// </summary>
public sealed class FullPipelineSyntheticMiniProjectTests
{
    private readonly IReadOnlyList<TypedPredicateFinding> _typedFindings;
    private readonly IReadOnlyList<SargabilityFinding> _tier1Findings;
    private readonly IReadOnlyList<DynamicSqlFinding> _dynamicSqlFindings;

    public FullPipelineSyntheticMiniProjectTests()
    {
        var projectDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "mini_project");
        var files = SqlFileDiscovery.EnumerateSqlFiles(projectDir);
        var parser = new SqlScriptParser();
        var parseResults = files.Select(f => parser.ParseFile(f)).ToList();

        foreach (var result in parseResults)
        {
            Assert.False(result.HasErrors, $"{result.SourcePath}: {string.Join("; ", result.Errors.Select(e => e.Message))}");
        }

        var catalog = CatalogBuilder.Build(parseResults);
        var lineage = LineageResolver.Resolve(catalog, parseResults);

        _typedFindings = [.. parseResults.SelectMany(r => TypedPredicateExtractor.Extract(r, catalog, lineage).TypedFindings)];
        _tier1Findings = [.. parseResults.SelectMany(NonSargablePredicateScanner.Scan)];
        _dynamicSqlFindings = [.. parseResults.SelectMany(DynamicSqlScanner.Scan)];
    }

    private static IEnumerable<TypedPredicateFinding> Actionable(IEnumerable<TypedPredicateFinding> findings) =>
        findings.Where(f => f.Verdict != Verdict.SeekPreserved);

    [Fact]
    public void DirectTableScanForced_IsPlantedAndFound()
    {
        var finding = Assert.Single(Actionable(_typedFindings), f => f.Column.ColumnName == "DisplayName" && f.Column.TableQualifiedName == "dbo.Users");

        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal(0, finding.Column.Depth);
        Assert.True(finding.Column.Indexed);
    }

    [Fact]
    public void WindowsCollationRangeSeek_IsPlantedAndFound()
    {
        var finding = Assert.Single(Actionable(_typedFindings), f => f.Column.ColumnName == "Region");

        Assert.Equal(Verdict.RangeSeek, finding.Verdict);
        Assert.False(finding.Column.Indexed);
    }

    [Fact]
    public void DepthTwoThroughViewChain_IsPlantedAndFound()
    {
        var finding = Assert.Single(Actionable(_typedFindings), f => f.Column.ColumnName == "OrderCode");

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
        Assert.Single(Actionable(_typedFindings), f => f.Column.ColumnName == "DisplayName");
    }

    [Fact]
    public void Tier1FunctionWrappedColumn_IsPlantedAndFound()
    {
        var finding = Assert.Single(_tier1Findings);

        Assert.Equal(SargabilityFindingKind.FunctionWrappedColumn, finding.Kind);
        Assert.Equal("CreatedAt", finding.ColumnName);
        Assert.Equal("YEAR", finding.Detail);
    }

    [Fact]
    public void Tier1CleanTwin_SargableDateRange_ProducesNoFinding()
    {
        // Exactly one Tier-1 finding total across the whole mini-project (the YEAR() one) -
        // the sargable date-range clean twin must not add a second.
        Assert.Single(_tier1Findings);
    }

    [Fact]
    public void DynamicSqlLiteralAndVariable_AreBothPlantedAndFound()
    {
        Assert.Equal(2, _dynamicSqlFindings.Count);
        Assert.Contains(_dynamicSqlFindings, f => f.IsLiteralOnly);
        Assert.Contains(_dynamicSqlFindings, f => !f.IsLiteralOnly);
    }

    [Fact]
    public void DynamicSqlCleanTwin_OrdinaryProcCall_ProducesNoFinding()
    {
        // Exactly two dynamic SQL findings total (literal + variable) - the ordinary
        // EXEC dbo.usp_... proc call in the clean-twin proc must not add a third.
        Assert.Equal(2, _dynamicSqlFindings.Count);
    }
}
