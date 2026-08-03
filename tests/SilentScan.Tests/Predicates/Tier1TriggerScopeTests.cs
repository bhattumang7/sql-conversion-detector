using SilentScan.Core.Reporting;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Regression coverage for Tier-1's missing trigger overrides (ConstructCoverage.json-adjacent
/// gap: NonSargablePredicateScanner had CreateProcedureStatement/CreateFunctionStatement
/// overrides but no CreateTriggerStatement/AlterTriggerStatement/CreateOrAlterTriggerStatement
/// counterparts, unlike TypedPredicateExtractor which has all three). Before this fix, a
/// function-wrapped or leading-wildcard predicate inside a trigger body resolved under no scope
/// key at all in Tier-1 (a #temp table declared there wouldn't resolve), and inserted/deleted
/// were never visible to Tier-1 regardless of scope, only to the typed pass. Runs through
/// ScanReportBuilder, the same entry point production uses.
/// </summary>
public sealed class Tier1TriggerScopeTests
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
    public async Task FunctionWrappedInsertedColumn_ResolvesToTargetTableColumn_NotIndexed()
    {
        var report = await Scan("""
            CREATE TABLE dbo.Orders (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, INDEX IX_Orders_Code (Code));
            GO
            CREATE TRIGGER dbo.trg_Orders ON dbo.Orders AFTER INSERT AS
            BEGIN
                SELECT 1 FROM inserted WHERE UPPER(Code) = 'X';
            END;
            """);

        var finding = Assert.Single(report.Tier1Findings);
        Assert.Equal("dbo.Orders", finding.TableQualifiedName);
        Assert.Equal("Code", finding.ColumnName);

        // inserted/deleted are a version-store rowset with no index of their own, even though
        // the real table column IS indexed - Tier-1 must not inherit the real table's index.
        Assert.False(finding.Indexed);
    }

    [Fact]
    public async Task TempTableDeclaredInTriggerBody_ResolvesUnderTheTriggersOwnScope()
    {
        var report = await Scan("""
            CREATE TABLE dbo.Orders (Code VARCHAR(20) NOT NULL);
            GO
            CREATE TRIGGER dbo.trg_Orders ON dbo.Orders AFTER INSERT AS
            BEGIN
                CREATE TABLE #codes (Code VARCHAR(20) NOT NULL, INDEX IX_Codes_Code (Code));
                INSERT INTO #codes SELECT Code FROM inserted;
                SELECT 1 FROM #codes WHERE UPPER(Code) = 'X';
            END;
            """);

        var finding = Assert.Single(report.Tier1Findings);
        Assert.Equal("Code", finding.ColumnName);
        Assert.True(finding.Indexed);
    }
}
