using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class SparseColumnDisallowedTypeOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(SparseColumnDisallowedTypeOracleTests);

    protected override string Ddl => string.Empty;

    private static IReadOnlyList<SparseColumnDisallowedTypeFinding> Scan(string sql)
    {
        var parsed = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(parsed.HasErrors, string.Join("; ", parsed.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([parsed]);
        return SparseColumnDisallowedTypeScanner.Scan(catalog);
    }

    [Theory]
    [InlineData("NTEXT")]
    [InlineData("IMAGE")]
    [InlineData("GEOMETRY")]
    [InlineData("GEOGRAPHY")]
    [InlineData("ROWVERSION")]
    public async Task DisallowedType_EngineRejectsIt_ScannerMustFlag(string sqlType)
    {
        var ddl = $"CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, C1 {sqlType} SPARSE NULL);";

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(ddl, connection);

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(1731, exception.Number);

        var finding = Assert.Single(Scan(ddl));
        Assert.Equal("C1", finding.ColumnName);
    }

    [Theory]
    [InlineData("XML")]
    [InlineData("HIERARCHYID")]
    [InlineData("SQL_VARIANT")]
    [InlineData("VARBINARY(MAX)")]
    public async Task AllowedType_EngineAcceptsIt_ScannerMustNotFlag(string sqlType)
    {
        var ddl = $"CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, C1 {sqlType} SPARSE NULL);";

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(ddl, connection);
        await command.ExecuteNonQueryAsync();

        Assert.Empty(Scan(ddl));
    }
}
