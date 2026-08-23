using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class MultiReferencedCteScannerTests
{
    private static IReadOnlyList<MultiReferencedCteFinding> Scan(string sql)
    {
        var ddl = "CREATE TABLE dbo.T (Id INT NOT NULL, Val INT NULL);";
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        return MultiReferencedCteScanner.Scan(result);
    }

    [Fact]
    public void CteReferencedTwiceInMainBody_Fires()
    {
        var findings = Scan(
            "WITH cte AS (SELECT Id FROM dbo.T) SELECT a.Id FROM cte a JOIN cte b ON a.Id = b.Id;");

        var finding = Assert.Single(findings);
        Assert.Equal("cte", finding.CteName);
        Assert.Equal(2, finding.ReferenceCount);
    }

    [Fact]
    public void CteReferencedOnce_NeverFires()
    {
        var findings = Scan("WITH cte AS (SELECT Id FROM dbo.T) SELECT Id FROM cte;");

        Assert.Empty(findings);
    }

    [Fact]
    public void LaterCteReferencesEarlierCteTwice_Fires()
    {
        var findings = Scan(
            """
            WITH a AS (SELECT Id FROM dbo.T),
                 b AS (SELECT x.Id FROM a x JOIN a y ON x.Id = y.Id)
            SELECT Id FROM b;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("a", finding.CteName);
        Assert.Equal(2, finding.ReferenceCount);
    }

    [Fact]
    public void RecursiveCteSelfReference_NeverCountsTowardOwnReferenceCount()
    {
        var findings = Scan(
            """
            WITH cte AS (
                SELECT Id FROM dbo.T WHERE Id = 1
                UNION ALL
                SELECT t.Id FROM dbo.T t JOIN cte c ON t.Id = c.Id + 1
            )
            SELECT Id FROM cte;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void RecursiveCteReferencedTwiceDownstream_Fires()
    {
        var findings = Scan(
            """
            WITH cte AS (
                SELECT Id FROM dbo.T WHERE Id = 1
                UNION ALL
                SELECT t.Id FROM dbo.T t JOIN cte c ON t.Id = c.Id + 1
            )
            SELECT a.Id FROM cte a JOIN cte b ON a.Id = b.Id;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(2, finding.ReferenceCount);
    }

    [Fact]
    public void TwoIndependentCtesEachReferencedOnce_NeverFires()
    {
        var findings = Scan(
            """
            WITH a AS (SELECT Id FROM dbo.T), b AS (SELECT Id FROM dbo.T)
            SELECT a.Id FROM a JOIN b ON a.Id = b.Id;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void NoWithClause_NeverFires()
    {
        var findings = Scan("SELECT Id FROM dbo.T;");

        Assert.Empty(findings);
    }

    [Fact]
    public void CteReferencedThreeTimes_ReportsAllThreeLines()
    {
        var findings = Scan(
            """
            WITH cte AS (SELECT Id FROM dbo.T)
            SELECT a.Id FROM cte a JOIN cte b ON a.Id = b.Id JOIN cte c ON b.Id = c.Id;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(3, finding.ReferenceCount);
        Assert.Equal(3, finding.ReferenceLines.Count);
    }
}
