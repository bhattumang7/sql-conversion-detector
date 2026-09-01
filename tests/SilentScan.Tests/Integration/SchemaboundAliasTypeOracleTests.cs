using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class SchemaboundAliasTypeOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(SchemaboundAliasTypeOracleTests);

    protected override string Ddl => "CREATE TYPE dbo.PositiveInt FROM INT;";

    private static IReadOnlyList<SchemaboundAliasTypeFinding> Scan(string ddl, string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return SchemaboundAliasTypeScanner.Scan(result, catalog);
    }

    [Fact]
    public async Task SchemaboundFunction_AliasTypedParameter_IsRejectedByLiveEngine_SoScannerMustFlagIt()
    {
        const string sql = """
            CREATE FUNCTION dbo.DoubleValue(@x dbo.PositiveInt) RETURNS INT WITH SCHEMABINDING
            AS BEGIN RETURN @x * 2 END;
            """;

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(2792, exception.Number);

        var finding = Assert.Single(Scan(Ddl, sql));
        Assert.Equal(SchemaboundAliasTypeKind.Parameter, finding.Kind);
        Assert.Equal("@x", finding.MemberName);
    }

    [Fact]
    public async Task SchemaboundFunction_AliasTypedReturn_IsRejectedByLiveEngine_SoScannerMustFlagIt()
    {
        const string sql = """
            CREATE FUNCTION dbo.Identity1(@x INT) RETURNS dbo.PositiveInt WITH SCHEMABINDING
            AS BEGIN RETURN @x END;
            """;

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(2792, exception.Number);

        var finding = Assert.Single(Scan(Ddl, sql));
        Assert.Equal(SchemaboundAliasTypeKind.ReturnType, finding.Kind);
    }

    [Fact]
    public async Task SchemaboundFunction_AliasTypedTableColumn_IsRejectedByLiveEngine_SoScannerMustFlagIt()
    {
        const string sql = """
            CREATE FUNCTION dbo.ListOne() RETURNS @t TABLE (Col1 dbo.PositiveInt) WITH SCHEMABINDING
            AS BEGIN INSERT INTO @t VALUES(1); RETURN END;
            """;

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(2792, exception.Number);

        var finding = Assert.Single(Scan(Ddl, sql));
        Assert.Equal(SchemaboundAliasTypeKind.TableColumn, finding.Kind);
        Assert.Equal("Col1", finding.MemberName);
    }

    [Fact]
    public async Task SchemaboundFunction_SystemTypedParameter_IsAcceptedByLiveEngine_SoScannerMustNotFlagIt()
    {
        const string sql = """
            CREATE FUNCTION dbo.DoubleOk(@x INT) RETURNS INT WITH SCHEMABINDING
            AS BEGIN RETURN @x * 2 END;
            """;

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();

        Assert.Empty(Scan(Ddl, sql));
    }

    [Fact]
    public async Task NonSchemaboundFunction_AliasTypedParameter_IsAcceptedByLiveEngine_SoScannerMustNotFlagIt()
    {
        const string sql = """
            CREATE FUNCTION dbo.DoubleUnbound(@x dbo.PositiveInt) RETURNS INT
            AS BEGIN RETURN @x * 2 END;
            """;

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();

        Assert.Empty(Scan(Ddl, sql));
    }
}
