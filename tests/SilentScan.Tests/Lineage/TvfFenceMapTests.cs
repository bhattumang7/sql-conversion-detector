using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Tests.Lineage;

public sealed class TvfFenceMapTests
{
    private static (DatabaseCatalog Catalog, IReadOnlyList<ViewDefinition> Views) Build(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        var (views, _) = ViewDefinitionExtractor.Extract([result], catalog.DefaultCollation, catalog.TypeAliases);
        return (catalog, views);
    }

    [Fact]
    public void TwoViewLayersOverMstvf_DepthAccumulatesAndOriginStaysAtIntroducingLayer()
    {
        var (catalog, views) = Build("""
            CREATE FUNCTION dbo.fn_Fence()
            RETURNS @T TABLE (Id INT)
            AS
            BEGIN
                INSERT INTO @T (Id) SELECT 1;
                RETURN;
            END;
            GO
            CREATE VIEW dbo.vw_Inner
            AS
            SELECT Id FROM dbo.fn_Fence();
            GO
            CREATE VIEW dbo.vw_Outer
            AS
            SELECT Id FROM dbo.vw_Inner;
            """);

        var map = TvfFenceMap.Build(views, catalog);

        var inner = Assert.Contains("dbo.vw_Inner", map);
        Assert.Equal("dbo.fn_Fence", inner.FunctionQualifiedName);
        Assert.Equal(1, inner.Depth);
        Assert.Equal("test.sql", inner.OriginSourcePath);

        var outer = Assert.Contains("dbo.vw_Outer", map);
        Assert.Equal("dbo.fn_Fence", outer.FunctionQualifiedName);
        Assert.Equal(2, outer.Depth);

        Assert.Equal(inner.OriginSourcePath, outer.OriginSourcePath);
        Assert.Equal(inner.OriginLine, outer.OriginLine);
    }

    [Fact]
    public void ViewOverPlainTable_HasNoFenceEntry()
    {
        var (catalog, views) = Build("""
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            CREATE VIEW dbo.vw_Plain
            AS
            SELECT Id FROM dbo.T;
            """);

        var map = TvfFenceMap.Build(views, catalog);

        Assert.DoesNotContain("dbo.vw_Plain", map);
    }

    [Fact]
    public void CyclicViewPair_ResolvesToNoFenceRatherThanLooping()
    {
        var (catalog, views) = Build("""
            CREATE VIEW dbo.vw_A
            AS
            SELECT Id FROM dbo.vw_B;
            GO
            CREATE VIEW dbo.vw_B
            AS
            SELECT Id FROM dbo.vw_A;
            """);

        var map = TvfFenceMap.Build(views, catalog);

        Assert.DoesNotContain("dbo.vw_A", map);
        Assert.DoesNotContain("dbo.vw_B", map);
    }

    [Fact]
    public void CteSharingATvfFencingViewsName_DoesNotFalselyInheritTheFence()
    {

        var (catalog, views) = Build("""
            CREATE FUNCTION dbo.fn_Fence()
            RETURNS @T TABLE (Id INT)
            AS
            BEGIN
                INSERT INTO @T (Id) SELECT 1;
                RETURN;
            END;
            GO
            CREATE VIEW dbo.vw_Fenced
            AS
            SELECT Id FROM dbo.fn_Fence();
            GO
            CREATE VIEW dbo.vw_Coincidence
            AS
            WITH vw_Fenced AS (SELECT 1 AS Id)
            SELECT Id FROM vw_Fenced;
            """);

        var map = TvfFenceMap.Build(views, catalog);

        Assert.Contains("dbo.vw_Fenced", map);
        Assert.DoesNotContain("dbo.vw_Coincidence", map);
    }
}
