using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class NonSargablePredicateScannerDataModificationScopeTests
{
    private static IReadOnlyList<SargabilityFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        return NonSargablePredicateScanner.Scan(result, catalog, lineage);
    }

    private const string Schema = """
        CREATE TABLE dbo.Orders
        (
            OrderId INT NOT NULL PRIMARY KEY,
            CreatedDate DATETIME NOT NULL,
            CreatedYear AS YEAR(CreatedDate)
        );
        GO
        CREATE INDEX IX_Orders_CreatedYear ON dbo.Orders(CreatedYear);
        GO
        """;

    [Fact]
    public void UpdateWithNoExtraFromClause_TargetAliasStillResolvesToCatalogTable()
    {
        var findings = Scan(Schema + "\n" + """
            UPDATE dbo.Orders SET CreatedDate = CreatedDate WHERE YEAR(CreatedDate) = 2020;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void DeleteWithNoExtraFromClause_TargetAliasStillResolvesToCatalogTable()
    {
        var findings = Scan(Schema + "\n" + """
            DELETE FROM dbo.Orders WHERE YEAR(CreatedDate) = 2020;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void UpdateWithExtraFromClauseJoin_JoinedAliasResolvesButLacksTheComputedIndexSoFindingFires()
    {
        var findings = Scan(Schema + "\n" + """
            CREATE TABLE dbo.OrdersStaging (OrderId INT NOT NULL PRIMARY KEY, CreatedDate DATETIME NOT NULL);
            GO
            UPDATE t SET t.CreatedDate = s.CreatedDate
            FROM dbo.Orders t
            JOIN dbo.OrdersStaging s ON s.OrderId = t.OrderId
            WHERE YEAR(s.CreatedDate) = 2020;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.DateFunctionOnColumn, finding.Kind);
        Assert.Equal("CreatedDate", finding.ColumnName);
    }
}
