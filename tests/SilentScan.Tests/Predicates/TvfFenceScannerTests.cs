using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class TvfFenceScannerTests
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "tvf_fence");

    private static IReadOnlyList<TvfFenceFinding> ScanFixture(string fileName)
    {
        var path = Path.Combine(FixturesDir, fileName);
        var result = SqlScriptParser.ParseFile(path);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        var (views, _) = ViewDefinitionExtractor.Extract([result], catalog.DefaultCollation, catalog.TypeAliases);
        var fenceMap = TvfFenceMap.Build(views, catalog);
        return TvfFenceScanner.Scan(result, catalog, fenceMap);
    }

    [Fact]
    public void FromOrJoin_MultiStatementTvfJoinedDirectly_Fires()
    {
        var findings = ScanFixture("FROM_OR_JOIN_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(TvfFenceFindingKind.FromOrJoin, finding.Kind);
        Assert.Equal("dbo.fn_OrderLines", finding.FunctionQualifiedName);
        Assert.Equal(TableValuedFunctionKind.MultiStatement, finding.FunctionKind);
    }

    [Fact]
    public void FromOrJoin_InlineTvfCalledIdentically_DoesNotFire()
    {
        Assert.Empty(ScanFixture("FROM_OR_JOIN_clean.sql"));
    }

    [Fact]
    public void CorrelatedApply_ArgumentReferencesOuterColumn_Fires()
    {
        var findings = ScanFixture("CORRELATED_APPLY_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(TvfFenceFindingKind.CorrelatedApply, finding.Kind);
        Assert.Equal("dbo.fn_CustomerTier", finding.FunctionQualifiedName);
        Assert.NotNull(finding.CorrelatedOuterColumns);
        Assert.Contains("CustomerId", finding.CorrelatedOuterColumns);
    }

    [Fact]
    public void CorrelatedApply_OverInlineTvf_DoesNotFire()
    {
        Assert.Empty(ScanFixture("CORRELATED_APPLY_clean.sql"));
    }

    [Fact]
    public void CorrelatedApply_UncorrelatedArgument_ClassifiesAsFromOrJoinNotCorrelated()
    {
        var result = SqlScriptParser.ParseText("test.sql", """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, CustomerId INT NOT NULL);
            GO
            CREATE FUNCTION dbo.fn_CustomerTier(@CustomerId INT)
            RETURNS @Tier TABLE (TierName VARCHAR(20))
            AS
            BEGIN
                INSERT INTO @Tier (TierName) SELECT 'Gold';
                RETURN;
            END;
            GO
            SELECT o.OrderId, t.TierName
            FROM dbo.Orders o
            CROSS APPLY dbo.fn_CustomerTier(1) t;
            """);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        var findings = TvfFenceScanner.Scan(result, catalog, new Dictionary<string, TvfFenceOrigin>());

        var finding = Assert.Single(findings);
        Assert.Equal(TvfFenceFindingKind.FromOrJoin, finding.Kind);
    }

    [Fact]
    public void NestedUnderViewOrTvf_ViewWrapsCorrelatedFence_FiresAtOuterCallSite()
    {
        var findings = ScanFixture("NESTED_UNDER_VIEW_OR_TVF_fires.sql");

        var nested = Assert.Single(findings, f => f.Kind == TvfFenceFindingKind.NestedUnderViewOrTvf);
        Assert.Equal("dbo.vw_CustomerTier", nested.ReferencedObjectQualifiedName);
        Assert.Equal("dbo.fn_CustomerTier", nested.FunctionQualifiedName);
        Assert.Equal(1, nested.Depth);
        Assert.NotNull(nested.OriginSourcePath);

        Assert.Contains(findings, f => f.Kind == TvfFenceFindingKind.CorrelatedApply);
    }

    [Fact]
    public void NestedUnderViewOrTvf_OrdinaryViewOverBaseTable_DoesNotFire()
    {
        Assert.Empty(ScanFixture("NESTED_UNDER_VIEW_OR_TVF_clean.sql"));
    }

[Fact]
    public void NestedUnderViewOrTvf_ViaInlineTvfFunctionCallSyntax_Fires()
    {
        var findings = ScanFixture("NESTED_UNDER_VIEW_OR_TVF_via_inline_tvf_fires.sql");

        var nested = Assert.Single(findings, f => f.Kind == TvfFenceFindingKind.NestedUnderViewOrTvf);
        Assert.Equal("dbo.itvf_CustomerTierWrapper", nested.ReferencedObjectQualifiedName);
        Assert.Equal("dbo.fn_CustomerTier", nested.FunctionQualifiedName);
        Assert.Equal(1, nested.Depth);
    }

    [Fact]
    public void NestedUnderViewOrTvf_ViaInlineTvfWrappingAnotherInlineTvf_DoesNotFire()
    {
        Assert.Empty(ScanFixture("NESTED_UNDER_VIEW_OR_TVF_via_inline_tvf_clean.sql"));
    }

    [Fact]
    public void InsertExec_ExecutesProcedureIntoTable_Fires()
    {
        var findings = ScanFixture("INSERT_EXEC_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(TvfFenceFindingKind.InsertExec, finding.Kind);
        Assert.Equal("dbo.usp_GetOrderIds", finding.ReferencedObjectQualifiedName);
        Assert.Null(finding.FunctionQualifiedName);
    }

    [Fact]
    public void InsertExec_OrdinaryInsertSelect_DoesNotFire()
    {
        Assert.Empty(ScanFixture("INSERT_EXEC_clean.sql"));
    }

    [Fact]
    public void Standalone_LoneMultiStatementTvfReference_Fires()
    {
        var findings = ScanFixture("STANDALONE_fires.sql");

        var finding = Assert.Single(findings);
        Assert.Equal(TvfFenceFindingKind.Standalone, finding.Kind);
        Assert.Equal("dbo.fn_ActiveOrderIds", finding.FunctionQualifiedName);
    }

    [Fact]
    public void Standalone_LoneInlineTvfReference_DoesNotFire()
    {
        Assert.Empty(ScanFixture("STANDALONE_clean.sql"));
    }
}
