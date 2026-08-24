using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class NonSargablePredicateScannerMergeScopeTests
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
        CREATE TABLE dbo.OrdersStaging
        (
            OrderId INT NOT NULL PRIMARY KEY,
            CreatedDate DATETIME NOT NULL
        );
        GO
        """;

    [Fact]
    public void MergeTargetAlias_ResolvesToTargetTable_ComputedIndexSuppressesFinding()
    {
        var findings = Scan(Schema + "\n" + """
            MERGE dbo.Orders AS target
            USING dbo.OrdersStaging AS source
            ON target.OrderId = source.OrderId
            WHEN MATCHED AND YEAR(target.CreatedDate) = 2020 THEN UPDATE SET CreatedDate = source.CreatedDate;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void MergeSourceAlias_ResolvesToSourceTableNotTarget_LacksTheComputedIndexSoFindingFires()
    {
        var findings = Scan(Schema + "\n" + """
            MERGE dbo.Orders AS target
            USING dbo.OrdersStaging AS source
            ON target.OrderId = source.OrderId
            WHEN MATCHED AND YEAR(source.CreatedDate) = 2020 THEN UPDATE SET CreatedDate = source.CreatedDate;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(SargabilityFindingKind.DateFunctionOnColumn, finding.Kind);
        Assert.Equal("CreatedDate", finding.ColumnName);
    }
}
