using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class UnindexedTempTableUsageScannerTests
{
    private static IReadOnlyList<UnindexedTempTableUsageFinding> Scan(string sql)
    {
        var parseResult = SqlScriptParser.ParseText("proc.sql", sql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([parseResult]);
        return UnindexedTempTableUsageScanner.Scan(parseResult, catalog);
    }

    [Fact]
    public void SelectIntoThenJoin_NoIndex_Fires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.usp_Foo AS
            BEGIN
                SELECT Id, Code INTO #t FROM dbo.Source WHERE Flag = 1;
                SELECT s.* FROM dbo.Source2 s INNER JOIN #t t ON s.Code = t.Code;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(UnindexedTempTableUsageKind.JoinOperand, finding.Kind);
    }

    [Fact]
    public void SelectIntoThenFilteredInWhere_NoIndex_Fires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.usp_Foo AS
            BEGIN
                SELECT Id, Code INTO #t FROM dbo.Source WHERE Flag = 1;
                SELECT * FROM #t WHERE Code = 'X';
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(UnindexedTempTableUsageKind.FilteredInWhere, finding.Kind);
    }

    [Fact]
    public void SelectIntoThenCommaJoin_NoIndex_Fires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.usp_Foo AS
            BEGIN
                SELECT Id, Code INTO #t FROM dbo.Source WHERE Flag = 1;
                SELECT s.* FROM dbo.Source2 s, #t t WHERE s.Code = t.Code;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(UnindexedTempTableUsageKind.JoinOperand, finding.Kind);
    }

    [Fact]
    public void SelectIntoThenExplicitCrossJoin_NoIndex_Fires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.usp_Foo AS
            BEGIN
                SELECT Id, Code INTO #t FROM dbo.Source WHERE Flag = 1;
                SELECT s.* FROM dbo.Source2 s CROSS JOIN #t t WHERE s.Code = t.Code;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(UnindexedTempTableUsageKind.JoinOperand, finding.Kind);
    }

    [Fact]
    public void SelectIntoThenCommaJoin_WithIndexCreated_NeverFires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.usp_Foo AS
            BEGIN
                SELECT Id, Code INTO #t FROM dbo.Source WHERE Flag = 1;
                CREATE INDEX IX_t_Code ON #t (Code);
                SELECT s.* FROM dbo.Source2 s, #t t WHERE s.Code = t.Code;
            END
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void SelectIntoThenJoin_WithIndexCreated_NeverFires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.usp_Foo AS
            BEGIN
                SELECT Id, Code INTO #t FROM dbo.Source WHERE Flag = 1;
                CREATE INDEX IX_t_Code ON #t (Code);
                SELECT s.* FROM dbo.Source2 s INNER JOIN #t t ON s.Code = t.Code;
            END
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void SelectIntoNeverUsedAgain_NeverFires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.usp_Foo AS
            BEGIN
                SELECT Id, Code INTO #t FROM dbo.Source WHERE Flag = 1;
                SELECT * FROM #t;
            END
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void CreateTableTempThenJoin_NotSelectInto_NeverFires()
    {

        var findings = Scan("""
            CREATE PROCEDURE dbo.usp_Foo AS
            BEGIN
                CREATE TABLE #t (Id INT, Code VARCHAR(20));
                SELECT s.* FROM dbo.Source2 s INNER JOIN #t t ON s.Code = t.Code;
            END
            """);

        Assert.Empty(findings);
    }
}
