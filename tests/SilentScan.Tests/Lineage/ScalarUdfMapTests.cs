using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Tests.Lineage;

/// <summary>
/// <see cref="ScalarUdfMap"/> is the depth/origin machinery the scalar-UDF finding stream will
/// lean on for the "reached through view/iTVF expansion" case (docs/detection-checklist.md Tier
/// 1 #1) - the 603-iTVF headline detection, mirroring <see cref="TvfFenceMapTests"/>'s coverage
/// of the analogous MSTVF-as-fence machinery.
/// </summary>
public sealed class ScalarUdfMapTests
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
    public void TwoViewLayersOverScalarUdfInSelectList_DepthAccumulatesAndOriginStaysAtIntroducingLayer()
    {
        var (catalog, views) = Build("""
            CREATE FUNCTION dbo.fn_Compute(@x INT)
            RETURNS INT
            AS
            BEGIN
                RETURN @x + 1;
            END;
            GO
            CREATE VIEW dbo.vw_Inner
            AS
            SELECT Id, dbo.fn_Compute(Id) AS Computed FROM dbo.T;
            GO
            CREATE VIEW dbo.vw_Outer
            AS
            SELECT Id, Computed FROM dbo.vw_Inner;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            """);

        var map = ScalarUdfMap.Build(views, catalog);

        var inner = Assert.Contains("dbo.vw_Inner", map);
        Assert.Equal("dbo.fn_Compute", inner.FunctionQualifiedName);
        Assert.Equal(ScalarUdfContext.SelectList, inner.OriginContext);
        Assert.Equal(1, inner.Depth);
        Assert.Equal("test.sql", inner.OriginSourcePath);

        var outer = Assert.Contains("dbo.vw_Outer", map);
        Assert.Equal("dbo.fn_Compute", outer.FunctionQualifiedName);
        Assert.Equal(2, outer.Depth);
        Assert.Equal(inner.OriginSourcePath, outer.OriginSourcePath);
        Assert.Equal(inner.OriginLine, outer.OriginLine);
    }

    [Fact]
    public void ScalarUdfInWhereClause_ClassifiedAsPredicateContext()
    {
        var (catalog, views) = Build("""
            CREATE FUNCTION dbo.fn_IsActive(@x INT)
            RETURNS BIT
            AS
            BEGIN
                RETURN 1;
            END;
            GO
            CREATE VIEW dbo.vw_Filtered
            AS
            SELECT Id FROM dbo.T WHERE dbo.fn_IsActive(Id) = 1;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            """);

        var map = ScalarUdfMap.Build(views, catalog);

        var origin = Assert.Contains("dbo.vw_Filtered", map);
        Assert.Equal(ScalarUdfContext.Where, origin.OriginContext);
    }

    [Fact]
    public void PredicateAndProjectionCallsInSameBody_PredicateWins()
    {
        var (catalog, views) = Build("""
            CREATE FUNCTION dbo.fn_A(@x INT)
            RETURNS INT
            AS
            BEGIN
                RETURN @x;
            END;
            GO
            CREATE VIEW dbo.vw_Mixed
            AS
            SELECT Id, dbo.fn_A(Id) AS Computed FROM dbo.T WHERE dbo.fn_A(Id) > 0;
            GO
            CREATE TABLE dbo.T (Id INT NOT NULL);
            """);

        var map = ScalarUdfMap.Build(views, catalog);

        var origin = Assert.Contains("dbo.vw_Mixed", map);
        Assert.Equal(ScalarUdfContext.Where, origin.OriginContext);
    }

    [Fact]
    public void ViewOverPlainTable_HasNoEntry()
    {
        var (catalog, views) = Build("""
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            CREATE VIEW dbo.vw_Plain
            AS
            SELECT Id FROM dbo.T;
            """);

        var map = ScalarUdfMap.Build(views, catalog);

        Assert.DoesNotContain("dbo.vw_Plain", map);
    }

    [Fact]
    public void CyclicViewPair_ResolvesToNoEntryRatherThanLooping()
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

        var map = ScalarUdfMap.Build(views, catalog);

        Assert.DoesNotContain("dbo.vw_A", map);
        Assert.DoesNotContain("dbo.vw_B", map);
    }

    [Fact]
    public void MultiStatementTvfBody_IsNeverACarrier()
    {
        // MSTVFs are deliberately excluded from ViewDefinitionExtractor's own Views set (they
        // have no expandable body an optimizer sees) - this just locks in that a scalar UDF
        // called inside one never produces a ScalarUdfMap entry for it, matching the MSTVF-as-
        // fence stream's own opacity boundary.
        var (catalog, views) = Build("""
            CREATE FUNCTION dbo.fn_Compute(@x INT)
            RETURNS INT
            AS
            BEGIN
                RETURN @x + 1;
            END;
            GO
            CREATE FUNCTION dbo.fn_Mstvf()
            RETURNS @T TABLE (Id INT)
            AS
            BEGIN
                INSERT INTO @T (Id) SELECT dbo.fn_Compute(1);
                RETURN;
            END;
            """);

        var map = ScalarUdfMap.Build(views, catalog);

        Assert.DoesNotContain("dbo.fn_Mstvf", map);
    }
}
