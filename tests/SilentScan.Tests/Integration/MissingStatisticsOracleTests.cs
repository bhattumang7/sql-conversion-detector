using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;
using SilentScan.Verify.Catalog;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class MissingStatisticsAutoCreateDisabledOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(MissingStatisticsAutoCreateDisabledOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.T1 (A INT NOT NULL, B INT NOT NULL, C INT NOT NULL);
        GO
        CREATE INDEX IX_T1_AB ON dbo.T1 (A, B);
        GO
        ALTER DATABASE CURRENT SET AUTO_CREATE_STATISTICS OFF;
        GO
        """;

    [Fact]
    public async Task LeadingKeyColumn_Clean_NonLeadingAndUncoveredColumns_Fire()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        Assert.False(catalog.IsAutoCreateStatsOn);

        var parseResult = SqlScriptParser.ParseText(
            "test.sql",
            "SELECT 1 FROM dbo.T1 WHERE A = 1 AND B = 2 AND C = 3;");
        Assert.False(parseResult.HasErrors);

        var findings = MissingStatisticsScanner.Scan(parseResult, catalog);

        var flaggedColumns = findings.Select(f => f.ColumnName).ToHashSet();
        Assert.DoesNotContain("A", flaggedColumns);
        Assert.Contains("B", flaggedColumns);
        Assert.Contains("C", flaggedColumns);
    }
}

[Trait("Category", "Oracle")]
public sealed class MissingStatisticsAutoCreateEnabledOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(MissingStatisticsAutoCreateEnabledOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.T1 (A INT NOT NULL, B INT NOT NULL);
        GO
        """;

    [Fact]
    public async Task AutoCreateStatisticsOn_NeverFires()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        Assert.True(catalog.IsAutoCreateStatsOn);

        var parseResult = SqlScriptParser.ParseText(
            "test.sql",
            "SELECT 1 FROM dbo.T1 WHERE A = 1 AND B = 2;");
        Assert.False(parseResult.HasErrors);

        var findings = MissingStatisticsScanner.Scan(parseResult, catalog);

        Assert.Empty(findings);
    }
}

[Trait("Category", "Oracle")]
public sealed class MissingStatisticsSingleColumnStatisticOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(MissingStatisticsSingleColumnStatisticOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.T1 (A INT NOT NULL, B INT NOT NULL);
        GO
        CREATE STATISTICS ST_T1_B ON dbo.T1 (B);
        GO
        ALTER DATABASE CURRENT SET AUTO_CREATE_STATISTICS OFF;
        GO
        """;

    [Fact]
    public async Task ExplicitSingleColumnStatistic_Clean()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var parseResult = SqlScriptParser.ParseText("test.sql", "SELECT 1 FROM dbo.T1 WHERE B = 2;");
        Assert.False(parseResult.HasErrors);

        var findings = MissingStatisticsScanner.Scan(parseResult, catalog);

        Assert.Empty(findings);
    }
}
