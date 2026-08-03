using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Regression coverage for dynamic SQL scope propagation (formerly pinned in
/// KnownGapCharacterizationTests.DynamicSql_TempTableFromEnclosingProcScope_DoesNotResolveInsideReparsedText):
/// a reparsed EXEC/sp_executesql fragment has no CREATE PROCEDURE wrapper of its own, so a
/// #temp table or trigger inserted/deleted pseudo-table that resolves fine in the surrounding
/// STATIC body previously failed to resolve inside the dynamic text, even though it's the exact
/// same object. DynamicSqlScanner now records the enclosing scope
/// (<see cref="Core.Predicates.DynamicSqlScope"/>) and DynamicSqlPipeline threads it into both
/// NonSargablePredicateScanner and TypedPredicateExtractor. Runs through
/// <see cref="ScanReportBuilder"/>, the same entry point production uses.
/// </summary>
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

        Assert.Contains(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral);

        var scanForcedFindings = report.TypedFindings.Where(f => f.Verdict == Verdict.ScanForced).ToList();
        Assert.Equal(2, scanForcedFindings.Count);
        Assert.All(scanForcedFindings, f => Assert.True(f.Column.Indexed));

        // The dynamic occurrence keeps its call-site provenance; the static one has none.
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

        var finding = Assert.Single(report.TypedFindings, f => f.Verdict == Verdict.ScanForced);
        Assert.True(finding.Column.Indexed);
        Assert.NotNull(finding.DynamicSqlCallSite);
    }

    [Fact]
    public async Task TempTableDeclaredStatically_ResolvesTwoLevelsDeepInNestedDynamicSql()
    {
        // The outer EXEC's own text contains a further EXEC - scope must survive the recursive
        // re-scan (DynamicSqlPipeline.AnalyzeNested passes the outer script's own Scope into
        // the nested DynamicSqlScanner.Scan call), not just the first level of reparsing.
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

        var finding = Assert.Single(report.TypedFindings, f => f.Verdict == Verdict.ScanForced);
        Assert.True(finding.Column.Indexed);
    }

    [Fact]
    public async Task TriggerInsertedPseudoTable_ResolvesInsideExecStringLiteral()
    {
        // A distinct mechanism from the #temp case: TypedPredicateExtractor seeds the CTE
        // stack with the trigger's own inserted/deleted pseudo-tables before walking the
        // reparsed fragment, mirroring what VisitTriggerBody does for static SQL.
        var report = await Scan("""
            CREATE TABLE dbo.Orders (OrderCode varchar(20) NOT NULL, INDEX IX_OrderCode (OrderCode));
            GO
            CREATE TRIGGER dbo.trg_Orders ON dbo.Orders AFTER INSERT AS
            BEGIN
                EXEC('SELECT 1 FROM inserted WHERE OrderCode = N''A1''');
            END;
            """);

        var finding = Assert.Single(report.TypedFindings, f => f.Verdict == Verdict.ScanForced);
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);

        // inserted/deleted are a version-store rowset with no index of their own - the type is
        // real and usable for a verdict, but Indexed must never be claimed true for it.
        Assert.False(finding.Column.Indexed);
    }
}
