using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>docs/detection-checklist.md Tier 2 "Lineage-metric findings" - "Post-expansion join width". Catalog/lineage-only ranking of an exact structural fact (gap between written and expanded base-table count) - no oracle needed for the counting mechanism itself; the separate "optimizer gives up exhaustive search past N" absolute threshold is deliberately not claimed (see the finding's own doc comment).</summary>
public sealed class PostExpansionJoinWidthScannerTests
{
    private static IReadOnlyList<PostExpansionJoinWidthFinding> Scan(string ddl, string probe)
    {
        var ddlResult = SqlScriptParser.ParseText("ddl.sql", ddl);
        Assert.False(ddlResult.HasErrors, string.Join("; ", ddlResult.Errors.Select(e => e.Message)));

        var probeResult = SqlScriptParser.ParseText("probe.sql", probe);
        Assert.False(probeResult.HasErrors, string.Join("; ", probeResult.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([ddlResult, probeResult]);
        var (views, _) = ViewDefinitionExtractor.Extract([ddlResult, probeResult], catalog.DefaultCollation, catalog.TypeAliases, ledger: null);
        var expansionMap = ViewExpansionMap.Build(views, catalog);
        return PostExpansionJoinWidthScanner.Scan(probeResult, catalog, expansionMap);
    }

    private const string FiveTableFanOutDdl = """
        CREATE TABLE dbo.T1 (Id INT NOT NULL);
        GO
        CREATE TABLE dbo.T2 (Id INT NOT NULL);
        GO
        CREATE TABLE dbo.T3 (Id INT NOT NULL);
        GO
        CREATE TABLE dbo.T4 (Id INT NOT NULL);
        GO
        CREATE TABLE dbo.T5 (Id INT NOT NULL);
        GO
        CREATE VIEW dbo.vWide AS
            SELECT T1.Id FROM dbo.T1
            JOIN dbo.T2 ON T1.Id = T2.Id
            JOIN dbo.T3 ON T1.Id = T3.Id
            JOIN dbo.T4 ON T1.Id = T4.Id
            JOIN dbo.T5 ON T1.Id = T5.Id;
        """;

    [Fact]
    public void ViewExpandsToFiveBaseTables_WrittenOne_GapMeetsThreshold_Fires()
    {
        var findings = Scan(FiveTableFanOutDdl, "SELECT Id FROM dbo.vWide;");

        var finding = Assert.Single(findings);
        Assert.Equal(1, finding.WrittenCount);
        Assert.Equal(5, finding.ExpandedCount);
        Assert.Equal(["dbo.T1", "dbo.T2", "dbo.T3", "dbo.T4", "dbo.T5"], finding.ExpandedBaseTables);
    }

    [Fact]
    public void PlainBaseTableQuery_NeverFires()
    {
        var findings = Scan("CREATE TABLE dbo.T (Id INT NOT NULL);", "SELECT Id FROM dbo.T;");

        Assert.Empty(findings);
    }

    [Fact]
    public void SmallGapBelowThreshold_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T1 (Id INT NOT NULL);
            GO
            CREATE TABLE dbo.T2 (Id INT NOT NULL);
            GO
            CREATE VIEW dbo.vTwo AS SELECT T1.Id FROM dbo.T1 JOIN dbo.T2 ON T1.Id = T2.Id;
            """,
            "SELECT Id FROM dbo.vTwo;");

        // written=1, expanded=2, gap=1 - below the MinimumGap=3 threshold.
        Assert.Empty(findings);
    }

    [Fact]
    public void WrittenAndExpandedEqual_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T1 (Id INT NOT NULL);
            GO
            CREATE TABLE dbo.T2 (Id INT NOT NULL);
            """,
            "SELECT T1.Id FROM dbo.T1 JOIN dbo.T2 ON T1.Id = T2.Id;");

        Assert.Empty(findings);
    }

    [Fact]
    public void InflatingSourcesName_TheWideningView()
    {
        var findings = Scan(FiveTableFanOutDdl, "SELECT Id FROM dbo.vWide;");

        var finding = Assert.Single(findings);
        Assert.Contains("dbo.vWide", finding.InflatingSources);
    }

    [Fact]
    public void TwoWideningQueriesOnTheSameLine_HaveDistinctColumns()
    {
        // Two sibling FROM clauses sharing the same SourcePath, Line and ModuleQualifiedName
        // (top-level batch, no enclosing module) - the exact shape that made
        // ScanReportBuilder's OrderBy chain nondeterministic before Column joined it, since
        // Line alone can't tell these two findings apart.
        var findings = Scan(FiveTableFanOutDdl, "SELECT Id FROM dbo.vWide UNION ALL SELECT Id FROM dbo.vWide;");

        Assert.Equal(2, findings.Count);
        Assert.Equal(2, findings.Select(f => f.Column).Distinct().Count());
        Assert.All(findings, f => Assert.Equal(1, f.Line));
    }
}
