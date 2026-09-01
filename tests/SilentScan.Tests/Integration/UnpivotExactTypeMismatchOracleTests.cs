using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class UnpivotExactTypeMismatchOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(UnpivotExactTypeMismatchOracleTests);

    protected override string Ddl =>
        "CREATE TABLE dbo.Metric (Id INT NOT NULL PRIMARY KEY, A VARCHAR(10) NULL, B VARCHAR(20) NULL, C INT NULL, D BIGINT NULL, E VARCHAR(10) COLLATE Latin1_General_CI_AS NULL, F VARCHAR(10) COLLATE French_CI_AS NULL, G VARCHAR(10) NULL);";

    private static IReadOnlyList<UnpivotExactTypeMismatchFinding> Scan(string ddl, string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return UnpivotExactTypeMismatchScanner.Scan(result, catalog);
    }

    [Fact]
    public async Task DifferentVarcharLengths_IsRejectedByLiveEngine_SoScannerMustFlagIt()
    {
        const string sql = "SELECT * FROM dbo.Metric UNPIVOT (Val FOR ColName IN (A, B)) AS u;";

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteReaderAsync());
        Assert.Equal(8167, exception.Number);

        var finding = Assert.Single(Scan(Ddl, sql));
        Assert.Equal("A", finding.ReferenceColumnName);
        Assert.Equal("B", finding.MismatchedColumnName);
    }

    [Fact]
    public async Task IntVsBigint_IsRejectedByLiveEngine_SoScannerMustFlagIt()
    {
        const string sql = "SELECT * FROM dbo.Metric UNPIVOT (Val FOR ColName IN (C, D)) AS u;";

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteReaderAsync());
        Assert.Equal(8167, exception.Number);

        var finding = Assert.Single(Scan(Ddl, sql));
        Assert.Equal("C", finding.ReferenceColumnName);
        Assert.Equal("D", finding.MismatchedColumnName);
    }

    [Fact]
    public async Task SameLengthDifferentCollation_IsRejectedByLiveEngine_SoScannerMustFlagIt()
    {
        const string sql = "SELECT * FROM dbo.Metric UNPIVOT (Val FOR ColName IN (E, F)) AS u;";

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteReaderAsync());
        Assert.Equal(8167, exception.Number);

        var finding = Assert.Single(Scan(Ddl, sql));
        Assert.Equal("E", finding.ReferenceColumnName);
        Assert.Equal("F", finding.MismatchedColumnName);
    }

    [Fact]
    public async Task SameVarcharLength_IsAcceptedByLiveEngine_SoScannerMustNotFlagIt()
    {
        const string sql = "SELECT * FROM dbo.Metric UNPIVOT (Val FOR ColName IN (A, G)) AS u;";

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.Empty(Scan(Ddl, sql));
    }
}
