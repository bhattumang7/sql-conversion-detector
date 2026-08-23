using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class FloatEqualityPredicateScannerTests
{
    private static IReadOnlyList<FloatEqualityFinding> Scan(string sql, string extraDdl = "")
    {
        var ddl =
            "CREATE TABLE dbo.Prices (Id INT NOT NULL PRIMARY KEY, Amount FLOAT NOT NULL, Rate REAL NOT NULL, Name VARCHAR(50) NOT NULL);" +
            "CREATE TABLE dbo.Other (Id INT NOT NULL PRIMARY KEY, Amount FLOAT NOT NULL);" +

            (extraDdl.Length > 0 ? $"\nGO\n{extraDdl}" : string.Empty);
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return FloatEqualityPredicateScanner.Scan(result, catalog);
    }

    [Fact]
    public void EqualityAgainstFloatColumn_LiteralOnRight_Fires()
    {
        var findings = Scan("SELECT * FROM dbo.Prices WHERE Amount = 1.5;");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Prices", finding.TableQualifiedName);
        Assert.Equal("Amount", finding.ColumnName);
    }

    [Fact]
    public void EqualityAgainstRealColumn_Fires()
    {
        var findings = Scan("SELECT * FROM dbo.Prices WHERE Rate = 1.5;");

        var finding = Assert.Single(findings);
        Assert.Equal("Rate", finding.ColumnName);
    }

    [Fact]
    public void EqualityAgainstFloatColumn_LiteralOnLeft_Fires()
    {
        var findings = Scan("SELECT * FROM dbo.Prices WHERE 1.5 = Amount;");

        Assert.Single(findings);
    }

    [Fact]
    public void EqualityAgainstFloatColumn_QualifiedByAlias_Fires()
    {
        var findings = Scan("SELECT * FROM dbo.Prices p WHERE p.Amount = 1.5;");

        Assert.Single(findings);
    }

    [Fact]
    public void EqualityAgainstFloatColumn_InJoinOnClause_Fires()
    {
        var findings = Scan(
            "SELECT * FROM dbo.Prices p JOIN dbo.Other o ON p.Amount = o.Amount WHERE p.Id = o.Id;");

        Assert.Single(findings);
    }

    [Fact]
    public void EqualityAgainstFloatColumn_InUpdateWhere_Fires()
    {
        var findings = Scan("UPDATE dbo.Prices SET Name = 'x' WHERE Amount = 1.5;");

        Assert.Single(findings);
    }

    [Fact]
    public void EqualityAgainstFloatColumn_InDeleteWhere_Fires()
    {
        var findings = Scan("DELETE FROM dbo.Prices WHERE Amount = 1.5;");

        Assert.Single(findings);
    }

    [Fact]
    public void PositionedUpdateWhereCurrentOfCursor_NullSearchCondition_NeverThrows()
    {

        var findings = Scan(
            "DECLARE cur CURSOR FOR SELECT Id FROM dbo.Prices; "
            + "OPEN cur; FETCH NEXT FROM cur; "
            + "UPDATE dbo.Prices SET Name = 'x' WHERE CURRENT OF cur; "
            + "CLOSE cur; DEALLOCATE cur;");

        Assert.Empty(findings);
    }

    [Fact]
    public void PositionedDeleteWhereCurrentOfCursor_NullSearchCondition_NeverThrows()
    {
        var findings = Scan(
            "DECLARE cur CURSOR FOR SELECT Id FROM dbo.Prices; "
            + "OPEN cur; FETCH NEXT FROM cur; "
            + "DELETE FROM dbo.Prices WHERE CURRENT OF cur; "
            + "CLOSE cur; DEALLOCATE cur;");

        Assert.Empty(findings);
    }

    [Fact]
    public void EqualityAgainstNonFloatColumn_NeverFires()
    {
        var findings = Scan("SELECT * FROM dbo.Prices WHERE Id = 1;");

        Assert.Empty(findings);
    }

    [Fact]
    public void RangeComparisonAgainstFloatColumn_NeverFires()
    {

        var findings = Scan("SELECT * FROM dbo.Prices WHERE Amount > 1.5;");

        Assert.Empty(findings);
    }

    [Fact]
    public void EqualityAgainstFloatColumn_InsideSubquery_ResolvesWithSubqueryOwnScope()
    {
        var findings = Scan(
            "SELECT * FROM dbo.Other o WHERE o.Id IN (SELECT p.Id FROM dbo.Prices p WHERE p.Amount = 1.5);");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Prices", finding.TableQualifiedName);
    }

    [Fact]
    public void EqualityAgainstFloatColumn_ThroughView_NotAnalyzed()
    {

        var findings = Scan(
            "SELECT * FROM dbo.PricesView WHERE Amount = 1.5;",
            extraDdl: "CREATE VIEW dbo.PricesView AS SELECT Amount FROM dbo.Prices;");

        Assert.Empty(findings);
    }
}
