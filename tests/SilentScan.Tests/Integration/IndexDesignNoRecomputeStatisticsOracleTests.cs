using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;
using SilentScan.Verify.Catalog;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class IndexDesignNoRecomputeStatisticsOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(IndexDesignNoRecomputeStatisticsOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.T1 (A INT NOT NULL, B INT NOT NULL);
        GO
        CREATE INDEX IX_T1_A ON dbo.T1 (A) WITH (STATISTICS_NORECOMPUTE = ON);
        GO
        CREATE INDEX IX_T1_B ON dbo.T1 (B);
        GO
        """;

    [Fact]
    public async Task IndexBuiltWithStatisticsNorecomputeOn_FiresOnThatIndexOnly()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var findings = IndexDesignScanner.Scan(catalog);

        var noRecomputeFindings = findings.Where(f => f.Kind == IndexDesignFindingKind.NoRecomputeStatistics).ToList();
        var finding = Assert.Single(noRecomputeFindings);
        Assert.Equal("IX_T1_A", finding.IndexName);
        Assert.DoesNotContain(noRecomputeFindings, f => f.IndexName == "IX_T1_B");
    }
}
