using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class FullTextPredicateInAggregateScannerTests
{
    private static IReadOnlyList<FullTextPredicateInAggregateFinding> Scan(string sql)
    {
        var ddl = "CREATE TABLE dbo.Ticket (Id INT NOT NULL, Notes NVARCHAR(4000) NULL);";
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return FullTextPredicateInAggregateScanner.Scan(result, catalog);
    }

    [Fact]
    public void ContainsInsideSumCase_Fires()
    {
        var findings = Scan("SELECT SUM(CASE WHEN CONTAINS(Notes, 'urgent') THEN 1 ELSE 0 END) FROM dbo.Ticket;");

        var finding = Assert.Single(findings);
        Assert.Equal("SUM", finding.AggregateFunctionName);
        Assert.Equal("CONTAINS", finding.FullTextFunctionName);
    }

    [Fact]
    public void FreetextInsideCountCase_Fires()
    {
        var findings = Scan("SELECT COUNT(CASE WHEN FREETEXT(Notes, 'urgent') THEN 1 END) FROM dbo.Ticket;");

        var finding = Assert.Single(findings);
        Assert.Equal("COUNT", finding.AggregateFunctionName);
        Assert.Equal("FREETEXT", finding.FullTextFunctionName);
    }

    [Theory]
    [InlineData("AVG")]
    [InlineData("MIN")]
    [InlineData("MAX")]
    public void ContainsInsideOtherAggregateCase_Fires(string aggregate)
    {
        var findings = Scan($"SELECT {aggregate}(CASE WHEN CONTAINS(Notes, 'urgent') THEN Id ELSE 0 END) FROM dbo.Ticket;");

        var finding = Assert.Single(findings);
        Assert.Equal(aggregate, finding.AggregateFunctionName);
    }

    [Fact]
    public void ContainsInWhereClause_DoesNotFire()
    {
        var findings = Scan("SELECT COUNT(*) FROM dbo.Ticket WHERE CONTAINS(Notes, 'urgent');");

        Assert.Empty(findings);
    }

    [Fact]
    public void ContainsInHavingClause_DoesNotFire()
    {
        var findings = Scan("SELECT Id, COUNT(*) FROM dbo.Ticket GROUP BY Id, Notes HAVING CONTAINS(Notes, 'urgent');");

        Assert.Empty(findings);
    }

    [Fact]
    public void ContainsInsideWindowedAggregate_DoesNotFire()
    {
        var findings = Scan("SELECT SUM(CASE WHEN CONTAINS(Notes, 'urgent') THEN 1 ELSE 0 END) OVER (PARTITION BY Id) FROM dbo.Ticket;");

        Assert.Empty(findings);
    }

    [Fact]
    public void NonAggregateFunctionCall_WithContainsArgument_DoesNotFire()
    {
        var findings = Scan("SELECT ISNULL(CASE WHEN CONTAINS(Notes, 'urgent') THEN 1 ELSE 0 END, 0) FROM dbo.Ticket;");

        Assert.Empty(findings);
    }
}
