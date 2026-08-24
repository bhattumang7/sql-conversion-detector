using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class FloatOrderDependentAggregateScannerTests
{
    private static IReadOnlyList<FloatOrderDependentAggregateFinding> Scan(string sql, string extraDdl = "")
    {
        var ddl =
            "CREATE TABLE dbo.Measurements (Id INT NOT NULL PRIMARY KEY, Amount FLOAT NOT NULL, Rate REAL NOT NULL, Quantity INT NOT NULL);" +
            (extraDdl.Length > 0 ? $"\nGO\n{extraDdl}" : string.Empty);
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return FloatOrderDependentAggregateScanner.Scan(result, catalog);
    }

    [Fact]
    public void SumOfFloatColumn_Fires()
    {
        var findings = Scan("SELECT SUM(Amount) FROM dbo.Measurements;");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Measurements", finding.TableQualifiedName);
        Assert.Equal("Amount", finding.ColumnName);
        Assert.Equal("SUM", finding.AggregateFunctionName);
    }

    [Fact]
    public void AvgOfRealColumn_Fires()
    {
        var findings = Scan("SELECT AVG(Rate) FROM dbo.Measurements;");

        var finding = Assert.Single(findings);
        Assert.Equal("Rate", finding.ColumnName);
        Assert.Equal("AVG", finding.AggregateFunctionName);
    }

    [Theory]
    [InlineData("VAR")]
    [InlineData("VARP")]
    [InlineData("STDEV")]
    [InlineData("STDEVP")]
    public void OtherOrderDependentAggregatesOfFloatColumn_Fire(string aggregateFunction)
    {
        var findings = Scan($"SELECT {aggregateFunction}(Amount) FROM dbo.Measurements;");

        var finding = Assert.Single(findings);
        Assert.Equal(aggregateFunction, finding.AggregateFunctionName);
    }

    [Theory]
    [InlineData("MIN")]
    [InlineData("MAX")]
    [InlineData("COUNT")]
    public void OrderIndependentAggregatesOfFloatColumn_NeverFire(string aggregateFunction)
    {
        var findings = Scan($"SELECT {aggregateFunction}(Amount) FROM dbo.Measurements;");

        Assert.Empty(findings);
    }

    [Fact]
    public void SumOfIntegerColumn_NeverFires()
    {
        var findings = Scan("SELECT SUM(Quantity) FROM dbo.Measurements;");

        Assert.Empty(findings);
    }

    [Fact]
    public void SumOfFloatColumn_InHavingClause_Fires()
    {
        var findings = Scan(
            "SELECT Id FROM dbo.Measurements GROUP BY Id HAVING SUM(Amount) > 1.0;");

        var finding = Assert.Single(findings);
        Assert.Equal("SUM", finding.AggregateFunctionName);
    }

    [Fact]
    public void SumOfFloatColumn_QualifiedByAlias_Fires()
    {
        var findings = Scan("SELECT SUM(m.Amount) FROM dbo.Measurements m;");

        Assert.Single(findings);
    }

    [Fact]
    public void SumOfFloatColumn_WindowedOverClause_NotAnalyzed()
    {
        var findings = Scan("SELECT SUM(Amount) OVER (PARTITION BY Id) FROM dbo.Measurements;");

        Assert.Empty(findings);
    }

    [Fact]
    public void SumOfFloatColumn_ThroughView_NotAnalyzed()
    {
        var findings = Scan(
            "SELECT SUM(Amount) FROM dbo.MeasurementsView;",
            extraDdl: "CREATE VIEW dbo.MeasurementsView AS SELECT Amount FROM dbo.Measurements;");

        Assert.Empty(findings);
    }

    [Fact]
    public void SumOfExpressionOverFloatColumn_NotAnalyzed()
    {
        var findings = Scan("SELECT SUM(Amount * 2) FROM dbo.Measurements;");

        Assert.Empty(findings);
    }

    [Fact]
    public void SumOfFloatColumn_InsideSubquery_ResolvesWithSubqueryOwnScope()
    {
        var findings = Scan(
            "SELECT * FROM dbo.Measurements WHERE Id IN (SELECT TOP 1 Id FROM dbo.Measurements HAVING SUM(Amount) > 0);");

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Measurements", finding.TableQualifiedName);
    }
}
