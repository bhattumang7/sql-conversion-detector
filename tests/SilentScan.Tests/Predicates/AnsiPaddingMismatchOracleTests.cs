using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class AnsiPaddingMismatchOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(AnsiPaddingMismatchOracleTests);

    protected override string Ddl => """
        SET ANSI_PADDING OFF;
        CREATE TABLE dbo.NonPadded (Code VARCHAR(20));
        GO
        SET ANSI_PADDING ON;
        CREATE TABLE dbo.Padded (Code VARCHAR(20));
        GO
        """;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var seedCommand = new SqlCommand(
            "INSERT INTO dbo.NonPadded VALUES ('abc   '); INSERT INTO dbo.Padded VALUES ('abc   ');", connection);
        await seedCommand.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task NonPaddedColumn_StripsTrailingBlanksAtInsertTime()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "SELECT DATALENGTH(Code) FROM dbo.NonPadded UNION ALL SELECT DATALENGTH(Code) FROM dbo.Padded;", connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(3, reader.GetInt32(0));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(6, reader.GetInt32(0));
    }

    [Fact]
    public async Task NonPaddedColumn_LikePatternWithTrailingWhitespace_NeverMatches()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var nonPaddedCommand = new SqlCommand("SELECT COUNT(*) FROM dbo.NonPadded WHERE Code LIKE 'abc ';", connection);
        var nonPaddedCount = (int)(await nonPaddedCommand.ExecuteScalarAsync())!;
        Assert.Equal(0, nonPaddedCount);

        await using var paddedCommand = new SqlCommand("SELECT COUNT(*) FROM dbo.Padded WHERE Code LIKE 'abc ';", connection);
        var paddedCount = (int)(await paddedCommand.ExecuteScalarAsync())!;
        Assert.Equal(1, paddedCount);
    }

    [Fact]
    public async Task NonPaddedColumn_PlainEqualityAgainstPaddedColumnOrTrailingWhitespaceLiteral_StillMatches()
    {

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var crossColumnCommand = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.NonPadded np JOIN dbo.Padded p ON np.Code = p.Code;", connection);
        var crossColumnCount = (int)(await crossColumnCommand.ExecuteScalarAsync())!;
        Assert.Equal(1, crossColumnCount);

        await using var literalCommand = new SqlCommand("SELECT COUNT(*) FROM dbo.NonPadded WHERE Code = 'abc   ';", connection);
        var literalCount = (int)(await literalCommand.ExecuteScalarAsync())!;
        Assert.Equal(1, literalCount);
    }
}
