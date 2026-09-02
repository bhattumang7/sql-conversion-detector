using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class FloatOrderDependentAggregateScannerLiveOracleTests
{
    [Fact]
    public async Task LiveDeployment_SumOverArithmeticExpressionOfFloatColumn_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.Measurements (Id INT NOT NULL PRIMARY KEY, Amount FLOAT NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_SumArithmeticFloat AS
            BEGIN
                SELECT SUM(Amount * 2) FROM dbo.Measurements;
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<FloatOrderDependentAggregateFinding>("FloatOrderDependentAggregateScanner"));
        Assert.Equal("dbo.Measurements", finding.TableQualifiedName);
        Assert.Equal("Amount", finding.ColumnName);
        Assert.Equal("SUM", finding.AggregateFunctionName);
    }

    [Fact]
    public async Task LiveDeployment_SumOverIntegerColumnCastToFloat_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.Measurements (Id INT NOT NULL PRIMARY KEY, Quantity INT NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_SumCastToFloat AS
            BEGIN
                SELECT SUM(CAST(Quantity AS FLOAT)) FROM dbo.Measurements;
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<FloatOrderDependentAggregateFinding>("FloatOrderDependentAggregateScanner"));
        Assert.Equal("Quantity", finding.ColumnName);
    }

    [Fact]
    public async Task LiveDeployment_SumOverIntegerColumnPlusFloatLiteralConstant_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.Measurements (Id INT NOT NULL PRIMARY KEY, Quantity INT NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_SumPlusFloatConstant AS
            BEGIN
                SELECT SUM(Quantity + 1.5e0) FROM dbo.Measurements;
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<FloatOrderDependentAggregateFinding>("FloatOrderDependentAggregateScanner"));
        Assert.Equal("Quantity", finding.ColumnName);
    }

    [Fact]
    public async Task LiveDeployment_SumOverIntegerColumnCastToDecimal_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.Measurements (Id INT NOT NULL PRIMARY KEY, Amount FLOAT NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_SumCastToDecimal AS
            BEGIN
                SELECT SUM(CAST(Amount AS DECIMAL(18, 4))) FROM dbo.Measurements;
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<FloatOrderDependentAggregateFinding>("FloatOrderDependentAggregateScanner"));
    }

    [Fact]
    public async Task LiveDeployment_SumOverIntegerColumnPlusIntegerLiteral_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.Measurements (Id INT NOT NULL PRIMARY KEY, Quantity INT NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_SumPlusIntegerLiteral AS
            BEGIN
                SELECT SUM(Quantity + 1) FROM dbo.Measurements;
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<FloatOrderDependentAggregateFinding>("FloatOrderDependentAggregateScanner"));
    }
}
