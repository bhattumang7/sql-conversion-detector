using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class FloatEqualityPredicateScannerOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(FloatEqualityPredicateScannerOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.SensorReadings (ReadingId INT NOT NULL PRIMARY KEY, Threshold FLOAT NOT NULL);
        GO
        """;

    [Fact]
    public async Task FloatNotEqualPredicate_AgainstValueAPersonWouldCallTheSame_EngineReportsUnequal_ScannerMustFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "SELECT CASE WHEN (CAST(0.1 AS FLOAT) + CAST(0.2 AS FLOAT)) <> CAST(0.3 AS FLOAT) THEN 1 ELSE 0 END;",
            connection);
        var engineResult = (int)(await command.ExecuteScalarAsync())!;
        Assert.Equal(1, engineResult);

        var findings = Scan("SELECT ReadingId FROM dbo.SensorReadings WHERE Threshold <> 0.3;");

        var finding = Assert.Single(findings);
        Assert.Equal("Threshold", finding.ColumnName);
    }

    [Fact]
    public async Task IntegerNotEqualPredicate_NeverSubjectToFloatImprecision_ScannerMustNotFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "SELECT CASE WHEN (1 + 2) <> 3 THEN 1 ELSE 0 END;",
            connection);
        var engineResult = (int)(await command.ExecuteScalarAsync())!;
        Assert.Equal(0, engineResult);

        var findings = Scan("SELECT ReadingId FROM dbo.SensorReadings WHERE ReadingId <> 3;");

        Assert.Empty(findings);
    }

    private static IReadOnlyList<FloatEqualityFinding> Scan(string sql)
    {
        var ddl = "CREATE TABLE dbo.SensorReadings (ReadingId INT NOT NULL PRIMARY KEY, Threshold FLOAT NOT NULL);";
        var parsed = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{sql}");
        Assert.False(parsed.HasErrors, string.Join("; ", parsed.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([parsed]);
        return FloatEqualityPredicateScanner.Scan(parsed, catalog);
    }
}
