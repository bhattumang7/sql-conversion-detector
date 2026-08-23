using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class SelectStarViewScannerTests
{
    private static IReadOnlyList<SelectStarViewFinding> Scan(string ddl, string probe)
    {
        var ddlResult = SqlScriptParser.ParseText("ddl.sql", ddl);
        Assert.False(ddlResult.HasErrors, string.Join("; ", ddlResult.Errors.Select(e => e.Message)));

        var probeResult = SqlScriptParser.ParseText("probe.sql", probe);
        Assert.False(probeResult.HasErrors, string.Join("; ", probeResult.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([ddlResult, probeResult]);
        var lineage = LineageResolver.Resolve(catalog, [ddlResult, probeResult]);
        var (views, _) = ViewDefinitionExtractor.Extract([ddlResult, probeResult], catalog.DefaultCollation, catalog.TypeAliases, ledger: null);
        var expansionMap = ViewExpansionMap.Build(views, catalog);
        var candidates = SelectStarViewScanner.BuildCandidates(views, expansionMap, lineage);
        return SelectStarViewScanner.Scan(probeResult, catalog, lineage, candidates);
    }

    private const string TwoLevelStarViewDdl = """
        CREATE TABLE dbo.T (A INT NOT NULL, B INT NOT NULL, C INT NOT NULL);
        GO
        CREATE VIEW dbo.vInner AS SELECT A, B, C FROM dbo.T;
        GO
        CREATE VIEW dbo.vOuter AS SELECT * FROM dbo.vInner;
        """;

    [Fact]
    public void ConsumerSelectsStrictSubset_Fires()
    {
        var findings = Scan(TwoLevelStarViewDdl, "SELECT v.A FROM dbo.vOuter v;");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.vOuter", finding.ViewQualifiedName);
        Assert.Equal(["A"], finding.ConsumerSelectedColumns);
        Assert.Equal(["A", "B", "C"], finding.ViewFullColumns);
        Assert.Equal(1, finding.ViewDepth);
    }

    [Fact]
    public void ConsumerSelectsStar_NeverFires()
    {
        var findings = Scan(TwoLevelStarViewDdl, "SELECT v.* FROM dbo.vOuter v;");

        Assert.Empty(findings);
    }

    [Fact]
    public void ConsumerSelectsEveryColumnExplicitly_NeverFires()
    {
        var findings = Scan(TwoLevelStarViewDdl, "SELECT v.A, v.B, v.C FROM dbo.vOuter v;");

        Assert.Empty(findings);
    }

    [Fact]
    public void ViewOverBaseTableOnly_Depth0_NeverCandidate()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (A INT NOT NULL, B INT NOT NULL);
            GO
            CREATE VIEW dbo.vFlat AS SELECT * FROM dbo.T;
            """,
            "SELECT v.A FROM dbo.vFlat v;");

        Assert.Empty(findings);
    }

    [Fact]
    public void QualifiedAliasStar_SameFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (A INT NOT NULL, B INT NOT NULL, C INT NOT NULL);
            GO
            CREATE VIEW dbo.vInner AS SELECT A, B, C FROM dbo.T;
            GO
            CREATE VIEW dbo.vOuter AS SELECT i.* FROM dbo.vInner i;
            """,
            "SELECT v.A FROM dbo.vOuter v;");

        Assert.Single(findings);
    }

    [Fact]
    public void StarOnlyInsideDerivedSubquery_NeverCandidate()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (A INT NOT NULL, B INT NOT NULL, C INT NOT NULL);
            GO
            CREATE VIEW dbo.vInner AS SELECT A, B, C FROM dbo.T;
            GO
            CREATE VIEW dbo.vOuter AS SELECT x.A, x.B, x.C FROM (SELECT * FROM dbo.vInner) x;
            """,
            "SELECT v.A FROM dbo.vOuter v;");

        Assert.Empty(findings);
    }

    [Fact]
    public void ViewWithNoStarAtAll_NeverCandidate()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (A INT NOT NULL, B INT NOT NULL, C INT NOT NULL);
            GO
            CREATE VIEW dbo.vInner AS SELECT A, B, C FROM dbo.T;
            GO
            CREATE VIEW dbo.vOuter AS SELECT A, B, C FROM dbo.vInner;
            """,
            "SELECT v.A FROM dbo.vOuter v;");

        Assert.Empty(findings);
    }

    [Fact]
    public void UnqualifiedColumnAcrossMultipleSources_Declines()
    {
        var findings = Scan(
            TwoLevelStarViewDdl + "\nGO\nCREATE TABLE dbo.Other (A INT NOT NULL);",
            "SELECT A FROM dbo.vOuter v JOIN dbo.Other o ON v.A = o.A;");

        Assert.Empty(findings);
    }

    [Fact]
    public void ConsumerReferencesViaInlineTvfCallSyntax_StillFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (A INT NOT NULL, B INT NOT NULL, C INT NOT NULL);
            GO
            CREATE VIEW dbo.vInner AS SELECT A, B, C FROM dbo.T;
            GO
            CREATE FUNCTION dbo.fnOuter (@x INT) RETURNS TABLE AS RETURN (SELECT i.* FROM dbo.vInner i WHERE i.A = @x);
            """,
            "SELECT f.A FROM dbo.fnOuter(1) f;");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.fnOuter", finding.ViewQualifiedName);
    }

    [Fact]
    public void NoConsumerReferencesTheView_NeverFires()
    {
        var findings = Scan(TwoLevelStarViewDdl, "SELECT Id FROM (SELECT 1 AS Id) x;");

        Assert.Empty(findings);
    }

    [Fact]
    public void TwoConsumersOfTheSameViewOnOneLine_HaveDistinctColumns()
    {
        var findings = Scan(TwoLevelStarViewDdl, "SELECT v1.A FROM dbo.vOuter v1 UNION ALL SELECT v2.B FROM dbo.vOuter v2;");

        Assert.Equal(2, findings.Count);
        Assert.Equal(2, findings.Select(f => f.ConsumerColumn).Distinct().Count());
        Assert.All(findings, f => Assert.Equal(1, f.ConsumerLine));
    }
}
