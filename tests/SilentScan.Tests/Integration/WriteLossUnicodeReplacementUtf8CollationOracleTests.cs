using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

public sealed class WriteLossUnicodeReplacementUtf8CollationOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(WriteLossUnicodeReplacementUtf8CollationOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Utf8Target (v varchar(30) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL);
        CREATE TABLE dbo.NonUtf8Target (v varchar(30) COLLATE Latin1_General_100_CI_AS NOT NULL);
        """;

    [Fact]
    public async Task NationalLiteral_IntoUtf8CollationColumn_PreservesEveryCharacter_NoReplacement()
    {
        var stored = await InsertAndReadBackAsync("dbo.Utf8Target", "龍龍龍");

        Assert.Equal("龍龍龍", stored);
    }

    [Fact]
    public async Task NationalLiteral_IntoNonUtf8CollationColumn_ReplacesWithQuestionMarks()
    {
        var stored = await InsertAndReadBackAsync("dbo.NonUtf8Target", "龍龍龍");

        Assert.Equal("???", stored);
    }

    private async Task<string> InsertAndReadBackAsync(string table, string value)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = $"INSERT INTO {table} (v) VALUES (@v)";
            insert.Parameters.AddWithValue("@v", value);
            await insert.ExecuteNonQueryAsync();
        }

        await using var select = connection.CreateCommand();
        select.CommandText = $"SELECT v FROM {table}";
        return (string)(await select.ExecuteScalarAsync())!;
    }
}
