using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class ScalarUdfScannerFixtureTests
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "scalar_udf");

    private static IReadOnlyList<ScalarUdfFinding> ScanFixture(string fileName)
    {
        var path = Path.Combine(FixturesDir, fileName);
        var result = SqlScriptParser.ParseFile(path);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        var (views, _) = ViewDefinitionExtractor.Extract([result], catalog.DefaultCollation, catalog.TypeAliases);
        var scalarUdfMap = ScalarUdfMap.Build(views, catalog);
        return ScalarUdfScanner.Scan(result, catalog, scalarUdfMap);
    }

    [Fact]
    public void Predicate_ScalarUdfInWhereClause_Fires()
    {
        var findings = ScanFixture("PREDICATE_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(ScalarUdfFindingKind.PredicateInvocation, finding.Kind);
        Assert.Equal("dbo.discount_price", finding.FunctionQualifiedName);
        Assert.Equal(ScalarUdfContext.Where, finding.Context);
    }

    [Fact]
    public void Predicate_BuiltInAndUnregisteredCalls_NeverFire()
    {
        Assert.Empty(ScanFixture("PREDICATE_clean.sql"));
    }

    [Fact]
    public void SelectList_ScalarUdfInSelectList_Fires()
    {
        var findings = ScanFixture("SELECT_LIST_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(ScalarUdfFindingKind.ProjectionInvocation, finding.Kind);
        Assert.Equal(ScalarUdfContext.SelectList, finding.Context);
        Assert.Equal("dbo.FormatUsername", finding.FunctionQualifiedName);
    }

    [Fact]
    public void SelectList_InlineTvfInFrom_NeverFires()
    {
        Assert.Empty(ScanFixture("SELECT_LIST_clean.sql"));
    }

    [Fact]
    public void NestedUnderView_ScalarUdfInsideViewBody_FiresNestedAndDirect()
    {
        var findings = ScanFixture("NESTED_UNDER_VIEW_OR_TVF_fires.sql");

        var nested = Assert.Single(findings, f => f.Kind == ScalarUdfFindingKind.NestedUnderViewOrTvf);
        Assert.Equal("dbo.vw_LineItemPricing", nested.ReferencedObjectQualifiedName);
        Assert.Equal("dbo.discount_price", nested.FunctionQualifiedName);
        Assert.Equal(1, nested.Depth);

        Assert.Contains(findings, f => f.Kind == ScalarUdfFindingKind.ProjectionInvocation);
    }

    [Fact]
    public void NestedUnderView_OrdinaryViewOverBaseTable_NeverFires()
    {
        Assert.Empty(ScanFixture("NESTED_UNDER_VIEW_OR_TVF_clean.sql"));
    }

    [Fact]
    public void NestedUnderView_ViaInlineTvfWrapper_FiresNested()
    {
        var findings = ScanFixture("NESTED_UNDER_VIEW_OR_TVF_via_inline_tvf_fires.sql");

        var nested = Assert.Single(findings, f => f.Kind == ScalarUdfFindingKind.NestedUnderViewOrTvf);
        Assert.Equal("dbo.itvf_LineItemPricing", nested.ReferencedObjectQualifiedName);
        Assert.Equal("dbo.discount_price", nested.FunctionQualifiedName);
        Assert.Equal(1, nested.Depth);
    }

    [Fact]
    public void NestedUnderView_ViaInlineTvfWrapperOverPlainTable_NeverFires()
    {
        Assert.Empty(ScanFixture("NESTED_UNDER_VIEW_OR_TVF_via_inline_tvf_clean.sql"));
    }

    [Fact]
    public void NotInlineable_FunctionCallingGetDate_ReportsBlockerReason()
    {
        var findings = ScanFixture("NOT_INLINEABLE_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(ScalarUdfInlineability.NotInlineable, finding.Inlineability);
        Assert.Contains("GETDATE", finding.InlineabilityBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NotInlineable_CleanFunctionBody_ReportsUnknownNeverInlineable()
    {
        var findings = ScanFixture("NOT_INLINEABLE_clean.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(ScalarUdfInlineability.Unknown, finding.Inlineability);
    }

    [Fact]
    public void NonSchemaboundConstantArgs_AllLiteralArgsOnNonSchemaboundFunction_FlagsNotFolded()
    {
        var findings = ScanFixture("NON_SCHEMABOUND_CONSTANT_ARGS_fires.sql");

        var finding = Assert.Single(findings);
        Assert.True(finding.ConstantArgumentsNotFolded);
    }

    [Fact]
    public void NonSchemaboundConstantArgs_SchemaboundTwin_DoesNotFlag()
    {
        var findings = ScanFixture("NON_SCHEMABOUND_CONSTANT_ARGS_clean.sql");

        var finding = Assert.Single(findings);
        Assert.False(finding.ConstantArgumentsNotFolded);
    }

    [Fact]
    public void Clr_ExternalNameFunction_RegistersClrKind()
    {
        var findings = ScanFixture("CLR_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(ScalarUdfKind.Clr, finding.UdfKind);
        Assert.Equal(ScalarUdfInlineability.NotInlineable, finding.Inlineability);
    }
}
