using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class DeprecatedSyntaxSetRowcountOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(DeprecatedSyntaxSetRowcountOracleTests);

    protected override string Ddl => string.Empty;

    private static IReadOnlyList<DeprecatedSyntaxFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return DeprecatedSyntaxScanner.Scan(result);
    }

    [Fact]
    public async Task RealServer_NonzeroRowcountLeftActive_CapsALaterUnrelatedStatement()
    {
        var cappedCount = await ExecuteScalarAsync(
            """
            DECLARE @T TABLE (Id INT NOT NULL);
            SET ROWCOUNT 1;
            INSERT INTO @T (Id) VALUES (1), (2), (3);
            SET ROWCOUNT 0;
            SELECT COUNT(*) FROM @T;
            """);

        Assert.Equal(1, (int)cappedCount);

        var trueCount = await ExecuteScalarAsync(
            """
            DECLARE @T TABLE (Id INT NOT NULL);
            INSERT INTO @T (Id) VALUES (1), (2), (3);
            SELECT COUNT(*) FROM @T;
            """);

        Assert.Equal(3, (int)trueCount);
    }

    [Fact]
    public void Scanner_FlagsSetRowcount_AsPresentTenseRisk()
    {
        var findings = Scan("SET ROWCOUNT 1;");

        var finding = Assert.Single(findings, f => f.Kind == DeprecatedSyntaxFindingKind.DeprecatedSetRowcount);
        Assert.Contains("silently caps rows affected/returned", finding.DetailText, StringComparison.Ordinal);
    }

    private async Task<object> ExecuteScalarAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected a scalar result.");
    }
}
