using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

public sealed class ForXmlExplicitInlineXsdOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(ForXmlExplicitInlineXsdOracleTests);

    protected override string Ddl => string.Empty;

    [Fact]
    public async Task ExplicitWithXmlSchema_FailsAsNotImplemented()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync("""
            SELECT 1 AS Tag, NULL AS Parent, name AS [Row!1!name]
            FROM sys.objects
            FOR XML EXPLICIT, XMLSCHEMA;
            """));

        Assert.Contains("not yet implemented", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplicitWithoutXmlSchema_NegativeControl_Succeeds()
    {
        var exception = await Record.ExceptionAsync(() => ExecuteAsync("""
            SELECT 1 AS Tag, NULL AS Parent, name AS [Row!1!name]
            FROM sys.objects
            FOR XML EXPLICIT;
            """));

        Assert.Null(exception);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
