using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class DynamicSqlScopePropagationTests
{
    private static async Task<ScanReport> Scan(string sql)
    {
        var report = await EngineAuthoritativeScan.ScanAsync(sql, "SQL_Latin1_General_CP1_CI_AS");
        foreach (var file in report.ParseHealth.Files)
        {
            Assert.Empty(file.Errors);
        }

        return report;
    }

    [Fact]
    public async Task TempTableDeclaredStatically_ResolvesInsideExecStringLiteral()
    {
        var report = await Scan("""
            CREATE TABLE dbo.Widgets (WidgetCode varchar(25) NOT NULL, INDEX IX_WidgetCode (WidgetCode));
            GO
            CREATE PROCEDURE dbo.usp_DynamicTemp AS
            BEGIN
                CREATE TABLE #w (WidgetCode varchar(25) NOT NULL, INDEX IX_W (WidgetCode));
                INSERT INTO #w SELECT WidgetCode FROM dbo.Widgets;
                EXEC('SELECT 1 FROM #w WHERE WidgetCode = N''W1''');
                SELECT 1 FROM #w WHERE WidgetCode = N'W2';
            END;
            """);

        Assert.Contains(report.Find<DynamicSqlFinding>("DynamicSqlScanner"), f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral);

        var scanForcedFindings = report.Find<TypedPredicateFinding>("TypedPredicateExtractor").Where(f => f.Verdict == Verdict.ScanForced).ToList();
        Assert.Equal(2, scanForcedFindings.Count);
        Assert.All(scanForcedFindings, f => Assert.True(f.Column.Indexed));

        Assert.Contains(scanForcedFindings, f => f.DynamicSqlCallSite is not null);
        Assert.Contains(scanForcedFindings, f => f.DynamicSqlCallSite is null);
    }

    [Fact]
    public async Task TempTableDeclaredStatically_ResolvesInsideSpExecuteSql()
    {
        var report = await Scan("""
            CREATE TABLE dbo.Widgets (WidgetCode varchar(25) NOT NULL, INDEX IX_WidgetCode (WidgetCode));
            GO
            CREATE PROCEDURE dbo.usp_DynamicTemp AS
            BEGIN
                CREATE TABLE #w (WidgetCode varchar(25) NOT NULL, INDEX IX_W (WidgetCode));
                INSERT INTO #w SELECT WidgetCode FROM dbo.Widgets;
                EXEC sp_executesql N'SELECT 1 FROM #w WHERE WidgetCode = N''W1''';
            END;
            """);

        var finding = Assert.Single(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"), f => f.Verdict == Verdict.ScanForced);
        Assert.True(finding.Column.Indexed);
        Assert.NotNull(finding.DynamicSqlCallSite);
    }

    [Fact]
    public async Task TempTableDeclaredStatically_ResolvesTwoLevelsDeepInNestedDynamicSql()
    {

        var report = await Scan("""
            CREATE TABLE dbo.Widgets (WidgetCode varchar(25) NOT NULL, INDEX IX_WidgetCode (WidgetCode));
            GO
            CREATE PROCEDURE dbo.usp_DynamicTemp AS
            BEGIN
                CREATE TABLE #w (WidgetCode varchar(25) NOT NULL, INDEX IX_W (WidgetCode));
                INSERT INTO #w SELECT WidgetCode FROM dbo.Widgets;
                EXEC('EXEC(''SELECT 1 FROM #w WHERE WidgetCode = N''''W1''''; '')');
            END;
            """);

        var finding = Assert.Single(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"), f => f.Verdict == Verdict.ScanForced);
        Assert.True(finding.Column.Indexed);
    }

    [Fact]
    public async Task TriggerInsertedPseudoTable_ResolvesInsideExecStringLiteral()
    {

        var report = await Scan("""
            CREATE TABLE dbo.Orders (OrderCode varchar(20) NOT NULL, INDEX IX_OrderCode (OrderCode));
            GO
            CREATE TRIGGER dbo.trg_Orders ON dbo.Orders AFTER INSERT AS
            BEGIN
                EXEC('SELECT 1 FROM inserted WHERE OrderCode = N''A1''');
            END;
            """);

        var finding = Assert.Single(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"), f => f.Verdict == Verdict.ScanForced);
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);

        Assert.False(finding.Column.Indexed);
    }
}
