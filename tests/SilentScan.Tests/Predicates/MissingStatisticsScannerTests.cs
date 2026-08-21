using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates;

public sealed class MissingStatisticsScannerTests
{
    private static CatalogTable Table(IReadOnlyList<CatalogStatisticsInfo>? statistics = null) =>
        new(
            "dbo", "Orders", CatalogTableKind.Table,
            [Col("OrderId"), Col("Region"), Col("Status")],
            Indexes: [],
            SourcePath: "dbo.Orders", SourceLine: 1,
            Statistics: statistics);

    private static CatalogColumn Col(string name) => new(name, new SqlType(SqlTypeCategory.Int), IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false);

    private static IReadOnlyList<MissingStatisticsFinding> Scan(string sql, DatabaseCatalog catalog)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return MissingStatisticsScanner.Scan(result, catalog);
    }

    private static DatabaseCatalog Catalog(bool? isAutoCreateStatsOn, IReadOnlyList<CatalogStatisticsInfo>? statistics = null)
    {
        var catalog = new DatabaseCatalog { IsAutoCreateStatsOn = isAutoCreateStatsOn };
        catalog.AddOrReplace(Table(statistics));
        return catalog;
    }

    [Fact]
    public void AutoCreateStatsUnknown_FileMode_NeverFires()
    {
        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE Status = 5;", Catalog(isAutoCreateStatsOn: null));

        Assert.Empty(findings);
    }

    [Fact]
    public void AutoCreateStatsOn_NoStatisticsAtAll_StillClean()
    {
        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE Status = 5;", Catalog(isAutoCreateStatsOn: true));

        Assert.Empty(findings);
    }

    [Fact]
    public void AutoCreateStatsOff_NoApplicableStatistic_Fires()
    {
        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE Status = 5;", Catalog(isAutoCreateStatsOn: false));

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.Orders", finding.TableQualifiedName);
        Assert.Equal("Status", finding.ColumnName);
    }

    [Fact]
    public void AutoCreateStatsOff_SingleColumnStatisticOnPredicateColumn_Clean()
    {
        var statistics = new[] { new CatalogStatisticsInfo("_WA_Status", NoRecompute: false, IsAutoCreated: true, ["Status"]) };
        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE Status = 5;", Catalog(isAutoCreateStatsOn: false, statistics));

        Assert.Empty(findings);
    }

    [Fact]
    public void AutoCreateStatsOff_PredicateColumnIsLeadingKeyOfMultiColumnStatistic_Clean()
    {
        var statistics = new[] { new CatalogStatisticsInfo("IX_Region_Status", NoRecompute: false, IsAutoCreated: false, ["Region", "Status"]) };
        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE Region = 5;", Catalog(isAutoCreateStatsOn: false, statistics));

        Assert.Empty(findings);
    }

    [Fact]
    public void AutoCreateStatsOff_PredicateColumnIsNonLeadingKeyOfMultiColumnStatistic_StillFires()
    {
        // Oracle-confirmed: the engine still auto-creates its own single-column statistic for a
        // non-leading key column of an existing multi-column statistic when auto-create is on -
        // proving a non-leading occurrence does not count as coverage.
        var statistics = new[] { new CatalogStatisticsInfo("IX_Region_Status", NoRecompute: false, IsAutoCreated: false, ["Region", "Status"]) };
        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE Status = 5;", Catalog(isAutoCreateStatsOn: false, statistics));

        var finding = Assert.Single(findings);
        Assert.Equal("Status", finding.ColumnName);
    }

    [Fact]
    public void AutoCreateStatsOff_PredicateOnlyReachableThroughOr_Declines()
    {
        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE Status = 5 OR OrderId = 1;", Catalog(isAutoCreateStatsOn: false));

        Assert.Empty(findings);
    }

    [Fact]
    public void AutoCreateStatsOff_TwoDifferentUncoveredColumns_ReportsBoth()
    {
        var findings = Scan("SELECT 1 FROM dbo.Orders WHERE Region = 1 AND Status = 5;", Catalog(isAutoCreateStatsOn: false));

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.ColumnName == "Region");
        Assert.Contains(findings, f => f.ColumnName == "Status");
    }
}
