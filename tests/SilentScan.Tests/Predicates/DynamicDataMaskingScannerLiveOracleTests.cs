using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class DynamicDataMaskingScannerLiveOracleTests
{
    private const string Ddl = """
        CREATE TABLE dbo.MaskedOrders
        (
            OrderId  INT NOT NULL PRIMARY KEY,
            Total    INT MASKED WITH (FUNCTION = 'default()') NOT NULL,
            Placed   DATETIME MASKED WITH (FUNCTION = 'default()') NOT NULL,
            Region   VARCHAR(20) NOT NULL
        );
        """;

    [Fact]
    public async Task WhereEqualityOnMaskedColumn_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            $"{Ddl}\nGO\nCREATE PROCEDURE dbo.GetOrder AS SELECT OrderId FROM dbo.MaskedOrders WHERE Total = 100;",
            minimumConfidence: FindingConfidence.Medium);

        var finding = Assert.Single(
            report.Find<DynamicDataMaskingFinding>(nameof(DynamicDataMaskingScanner)),
            f => f.Kind == DynamicDataMaskingFindingKind.PredicateExposure);
        Assert.Equal("dbo.MaskedOrders", finding.TableQualifiedName);
        Assert.Equal("Total", finding.ColumnName);
        Assert.Equal("default", finding.MaskingFunctionName);
    }

    [Fact]
    public async Task WhereOnNonMaskedColumn_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            $"{Ddl}\nGO\nCREATE PROCEDURE dbo.GetOrder AS SELECT OrderId FROM dbo.MaskedOrders WHERE Region = 'West';",
            minimumConfidence: FindingConfidence.Medium);

        Assert.Empty(report.Find<DynamicDataMaskingFinding>(nameof(DynamicDataMaskingScanner)));
    }

    [Fact]
    public async Task WhereBetweenOnMaskedColumn_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            $"{Ddl}\nGO\nCREATE PROCEDURE dbo.GetOrder AS SELECT OrderId FROM dbo.MaskedOrders WHERE Total BETWEEN 1 AND 100;",
            minimumConfidence: FindingConfidence.Medium);

        Assert.Contains(
            report.Find<DynamicDataMaskingFinding>(nameof(DynamicDataMaskingScanner)),
            f => f.Kind == DynamicDataMaskingFindingKind.PredicateExposure && f.ContextDescription.Contains("BETWEEN"));
    }

    [Fact]
    public async Task WhereInListOnMaskedColumn_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            $"{Ddl}\nGO\nCREATE PROCEDURE dbo.GetOrder AS SELECT OrderId FROM dbo.MaskedOrders WHERE Total IN (100, 200);",
            minimumConfidence: FindingConfidence.Medium);

        Assert.Contains(
            report.Find<DynamicDataMaskingFinding>(nameof(DynamicDataMaskingScanner)),
            f => f.Kind == DynamicDataMaskingFindingKind.PredicateExposure && f.ContextDescription.Contains("IN list"));
    }

    [Fact]
    public async Task JoinOnClauseComparingMaskedColumn_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            $$"""
            {{Ddl}}
            GO
            CREATE TABLE dbo.MaskedOrderTotals (Total INT NOT NULL);
            GO
            CREATE PROCEDURE dbo.GetOrder AS
                SELECT o.OrderId FROM dbo.MaskedOrders o JOIN dbo.MaskedOrderTotals t ON o.Total = t.Total;
            """,
            minimumConfidence: FindingConfidence.Medium);

        Assert.Contains(
            report.Find<DynamicDataMaskingFinding>(nameof(DynamicDataMaskingScanner)),
            f => f.Kind == DynamicDataMaskingFindingKind.PredicateExposure && f.TableQualifiedName == "dbo.MaskedOrders");
    }

    [Fact]
    public async Task GroupByMaskedColumn_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            $"{Ddl}\nGO\nCREATE PROCEDURE dbo.GetOrder AS SELECT Total, COUNT(*) FROM dbo.MaskedOrders GROUP BY Total;",
            minimumConfidence: FindingConfidence.Medium);

        Assert.Contains(
            report.Find<DynamicDataMaskingFinding>(nameof(DynamicDataMaskingScanner)),
            f => f.Kind == DynamicDataMaskingFindingKind.PredicateExposure && f.ContextDescription == "GROUP BY");
    }

    [Fact]
    public async Task OrderByMaskedColumn_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            $"{Ddl}\nGO\nCREATE PROCEDURE dbo.GetOrder AS SELECT OrderId FROM dbo.MaskedOrders ORDER BY Total;",
            minimumConfidence: FindingConfidence.Medium);

        Assert.Contains(
            report.Find<DynamicDataMaskingFinding>(nameof(DynamicDataMaskingScanner)),
            f => f.Kind == DynamicDataMaskingFindingKind.PredicateExposure && f.ContextDescription == "ORDER BY");
    }

    [Fact]
    public async Task IsNullCheckOnMaskedColumn_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            $"{Ddl}\nGO\nCREATE PROCEDURE dbo.GetOrder AS SELECT OrderId FROM dbo.MaskedOrders WHERE Total IS NOT NULL;",
            minimumConfidence: FindingConfidence.Medium);

        Assert.DoesNotContain(
            report.Find<DynamicDataMaskingFinding>(nameof(DynamicDataMaskingScanner)),
            f => f.Kind == DynamicDataMaskingFindingKind.PredicateExposure);
    }

    [Fact]
    public async Task SelectMaskedColumnDirectly_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            $"{Ddl}\nGO\nCREATE PROCEDURE dbo.GetOrder AS SELECT Total FROM dbo.MaskedOrders;",
            minimumConfidence: FindingConfidence.Medium);

        Assert.Empty(report.Find<DynamicDataMaskingFinding>(nameof(DynamicDataMaskingScanner)));
    }

    [Fact]
    public async Task ArithmeticOverMaskedColumnInSelectList_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            $"{Ddl}\nGO\nCREATE PROCEDURE dbo.GetOrder AS SELECT Total + 1 AS AdjustedTotal FROM dbo.MaskedOrders;",
            minimumConfidence: FindingConfidence.Medium);

        var finding = Assert.Single(
            report.Find<DynamicDataMaskingFinding>(nameof(DynamicDataMaskingScanner)),
            f => f.Kind == DynamicDataMaskingFindingKind.ComputedExpressionCollapse);
        Assert.Equal("Total", finding.ColumnName);
    }

    [Fact]
    public async Task AggregateOverMaskedColumnInSelectList_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            $"{Ddl}\nGO\nCREATE PROCEDURE dbo.GetOrder AS SELECT SUM(Total) AS GrandTotal FROM dbo.MaskedOrders;",
            minimumConfidence: FindingConfidence.Medium);

        Assert.Contains(
            report.Find<DynamicDataMaskingFinding>(nameof(DynamicDataMaskingScanner)),
            f => f.Kind == DynamicDataMaskingFindingKind.ComputedExpressionCollapse && f.ColumnName == "Total");
    }

    [Fact]
    public async Task DateAddOverMaskedColumnInSelectList_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            $"{Ddl}\nGO\nCREATE PROCEDURE dbo.GetOrder AS SELECT DATEADD(day, 1, Placed) AS NextDay FROM dbo.MaskedOrders;",
            minimumConfidence: FindingConfidence.Medium);

        Assert.Contains(
            report.Find<DynamicDataMaskingFinding>(nameof(DynamicDataMaskingScanner)),
            f => f.Kind == DynamicDataMaskingFindingKind.ComputedExpressionCollapse && f.ColumnName == "Placed");
    }

    [Fact]
    public async Task AlterTableAddMaskedWith_IsTreatedAsMasked()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.LegacyOrders (OrderId INT NOT NULL PRIMARY KEY, Total INT NOT NULL);
            GO
            ALTER TABLE dbo.LegacyOrders ALTER COLUMN Total ADD MASKED WITH (FUNCTION = 'default()');
            GO
            CREATE PROCEDURE dbo.GetOrder AS SELECT OrderId FROM dbo.LegacyOrders WHERE Total = 100;
            """,
            minimumConfidence: FindingConfidence.Medium);

        Assert.Contains(
            report.Find<DynamicDataMaskingFinding>(nameof(DynamicDataMaskingScanner)),
            f => f.Kind == DynamicDataMaskingFindingKind.PredicateExposure && f.TableQualifiedName == "dbo.LegacyOrders");
    }

    [Fact]
    public async Task AlterTableDropMasked_IsNoLongerTreatedAsMasked()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.UnmaskedOrders (OrderId INT NOT NULL PRIMARY KEY, Total INT MASKED WITH (FUNCTION = 'default()') NOT NULL);
            GO
            ALTER TABLE dbo.UnmaskedOrders ALTER COLUMN Total DROP MASKED;
            GO
            CREATE PROCEDURE dbo.GetOrder AS SELECT OrderId FROM dbo.UnmaskedOrders WHERE Total = 100;
            """,
            minimumConfidence: FindingConfidence.Medium);

        Assert.Empty(report.Find<DynamicDataMaskingFinding>(nameof(DynamicDataMaskingScanner)));
    }

    [Fact]
    public async Task AlterTableAddMaskedWithHavingNoDataType_DoesNotDiscardTheColumnsDeclaredType()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.SensorReadings (ReadingId INT NOT NULL PRIMARY KEY, Value FLOAT NOT NULL);
            GO
            ALTER TABLE dbo.SensorReadings ALTER COLUMN Value ADD MASKED WITH (FUNCTION = 'default()');
            GO
            CREATE PROCEDURE dbo.GetTotal AS SELECT SUM(Value) AS Total FROM dbo.SensorReadings;
            """,
            minimumConfidence: FindingConfidence.Medium);

        Assert.Contains(
            report.Find<FloatOrderDependentAggregateFinding>("FloatOrderDependentAggregateScanner"),
            f => f.TableQualifiedName == "dbo.SensorReadings" && f.ColumnName == "Value");
    }

    [Fact]
    public async Task AlterColumnTypeChange_DropsMaskingState()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE TABLE dbo.WideningOrders (OrderId INT NOT NULL PRIMARY KEY, Total SMALLINT MASKED WITH (FUNCTION = 'default()') NOT NULL);
            GO
            ALTER TABLE dbo.WideningOrders ALTER COLUMN Total INT NOT NULL;
            GO
            CREATE PROCEDURE dbo.GetOrder AS SELECT OrderId FROM dbo.WideningOrders WHERE Total = 100;
            """,
            minimumConfidence: FindingConfidence.Medium);

        Assert.DoesNotContain(
            report.Find<DynamicDataMaskingFinding>(nameof(DynamicDataMaskingScanner)),
            f => f.TableQualifiedName == "dbo.WideningOrders");
    }
}
