using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class FullTextIndexDdlOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(FullTextIndexDdlOracleTests);

    protected override string Ddl => """
        CREATE FULLTEXT CATALOG DdlCatalog AS DEFAULT;
        GO
        CREATE TABLE dbo.TypeCheck (Id INT NOT NULL CONSTRAINT PK_TypeCheck PRIMARY KEY, Body NVARCHAR(4000) NOT NULL, Payload VARBINARY(100) NULL);
        GO
        CREATE TABLE dbo.LanguageCheck (Id INT NOT NULL CONSTRAINT PK_LanguageCheck PRIMARY KEY, Body NVARCHAR(4000) NOT NULL);
        GO
        CREATE TABLE dbo.DeterminismCheck (Id INT NOT NULL CONSTRAINT PK_DeterminismCheck PRIMARY KEY, Body VARCHAR(200) NULL, Tagged AS (Body + CONVERT(VARCHAR(30), GETDATE())));
        """;

    [Fact]
    public async Task UnsupportedColumnType_FixedLengthVarbinary_BlocksWith7670()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync("CREATE FULLTEXT INDEX ON dbo.TypeCheck(Payload) KEY INDEX PK_TypeCheck;"));

        Assert.Equal(7670, exception.Number);
    }

    [Fact]
    public async Task InvalidLanguageId_BlocksWith7696()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync("CREATE FULLTEXT INDEX ON dbo.LanguageCheck(Body LANGUAGE 999999) KEY INDEX PK_LanguageCheck;"));

        Assert.Equal(7696, exception.Number);
    }

    [Fact]
    public async Task NonDeterministicNonpersistedComputedColumn_BlocksWith9928()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync("CREATE FULLTEXT INDEX ON dbo.DeterminismCheck(Tagged) KEY INDEX PK_DeterminismCheck;"));

        Assert.Equal(9928, exception.Number);
    }

    [Fact]
    public async Task ValidSupportedColumn_Deploys()
    {
        await ExecuteAsync("CREATE FULLTEXT INDEX ON dbo.TypeCheck(Body) KEY INDEX PK_TypeCheck;");
    }

    [Fact]
    public void Scanner_AgreesWithTheLiveEngine_OnTheSameDdlText()
    {
        var result = SqlScriptParser.ParseText(
            "test.sql",
            $"""
            {Ddl}
            GO
            CREATE FULLTEXT INDEX ON dbo.TypeCheck(Payload) KEY INDEX PK_TypeCheck;
            CREATE FULLTEXT INDEX ON dbo.LanguageCheck(Body LANGUAGE 999999) KEY INDEX PK_LanguageCheck;
            CREATE FULLTEXT INDEX ON dbo.DeterminismCheck(Tagged) KEY INDEX PK_DeterminismCheck;
            """);

        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        var findings = FullTextIndexDdlScanner.Scan(catalog);

        Assert.Contains(findings, f => f.Kind == FullTextIndexDdlFindingKind.UnsupportedColumnType && f.TableQualifiedName == "dbo.TypeCheck" && f.ColumnName == "Payload");
        Assert.Contains(findings, f => f.Kind == FullTextIndexDdlFindingKind.InvalidLanguageId && f.TableQualifiedName == "dbo.LanguageCheck" && f.ColumnName == "Body");
        Assert.Contains(findings, f => f.Kind == FullTextIndexDdlFindingKind.NonDeterministicComputedColumn && f.TableQualifiedName == "dbo.DeterminismCheck" && f.ColumnName == "Tagged");
        Assert.DoesNotContain(findings, f => f.TableQualifiedName == "dbo.TypeCheck" && f.ColumnName == "Body");
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
