using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class DeprecatedSyntaxLegacyCompatibilityViewOracleTests : OracleTestFixture
{
    private const int InvalidObjectNameErrorNumber = 208;

    protected override string DatabaseNameSeed => nameof(DeprecatedSyntaxLegacyCompatibilityViewOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.syslocks (Id INT NOT NULL PRIMARY KEY);
        INSERT INTO dbo.syslocks (Id) VALUES (1);
        """;

    private static IReadOnlyList<DeprecatedSyntaxFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return DeprecatedSyntaxScanner.Scan(result);
    }

    [Fact]
    public async Task RealServer_SysSyslocks_IsNotARealCatalogObject()
    {
        var objectId = await ExecuteScalarAsync("SELECT OBJECT_ID('sys.syslocks');");
        Assert.Equal(DBNull.Value, objectId);

        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            ExecuteScalarAsync("SELECT * FROM sys.syslocks;"));

        Assert.Equal(InvalidObjectNameErrorNumber, ex.Number);
    }

    [Fact]
    public async Task RealServer_SysSyslockinfo_NegativeControl_IsARealCatalogObject()
    {
        var objectId = await ExecuteScalarAsync("SELECT OBJECT_ID('sys.syslockinfo');");

        Assert.NotEqual(DBNull.Value, objectId);
    }

    [Fact]
    public async Task RealServer_OrdinaryTableNamedSyslocks_ScansCleanAfterFix()
    {
        var rowCount = await ExecuteScalarAsync("SELECT COUNT(*) FROM dbo.syslocks;");
        Assert.Equal(1, (int)rowCount);

        var findings = Scan("SELECT * FROM syslocks;");

        Assert.DoesNotContain(findings, f => f.Kind == DeprecatedSyntaxFindingKind.LegacySystemCompatibilityView);
    }

    [Fact]
    public void Syslockinfo_NegativeControl_StillFlaggedByScanner()
    {
        var findings = Scan("SELECT * FROM syslockinfo;");

        Assert.Contains(findings, f => f.Kind == DeprecatedSyntaxFindingKind.LegacySystemCompatibilityView);
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
