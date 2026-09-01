using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class WindowFunctionArgumentTableSamplePercentOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(WindowFunctionArgumentTableSamplePercentOracleTests);

    protected override string Ddl => "CREATE TABLE dbo.Sales (Id INT NOT NULL PRIMARY KEY, Amt INT NOT NULL);";

    private static IReadOnlyList<WindowFunctionArgumentFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return WindowFunctionArgumentScanner.Scan(result);
    }

    [Theory]
    [InlineData(150)]
    [InlineData(-1)]
    public async Task TableSamplePercentOutOfInclusiveRange_IsRejectedByLiveEngine_SoScannerMustFlagIt(int percent)
    {
        var sql = $"SELECT COUNT(*) FROM dbo.Sales TABLESAMPLE ({percent} PERCENT);";

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());
        Assert.Equal(476, exception.Number);

        var findings = Scan(sql);
        var finding = Assert.Single(findings);
        Assert.Equal(WindowFunctionArgumentFindingKind.TableSamplePercentOutOfRange, finding.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task TableSamplePercentAtInclusiveBoundary_IsAcceptedByLiveEngine_SoScannerMustNotFlagIt(int percent)
    {
        var sql = $"SELECT COUNT(*) FROM dbo.Sales TABLESAMPLE ({percent} PERCENT);";

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);

        await command.ExecuteScalarAsync();

        Assert.Empty(Scan(sql));
    }
}
