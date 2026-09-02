using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;
using SilentScan.Verify.Catalog;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class SemanticSearchOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(SemanticSearchOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.PlainText (Id INT NOT NULL CONSTRAINT PK_PlainText PRIMARY KEY, Body NVARCHAR(4000) NOT NULL);
        GO
        CREATE TABLE dbo.NoFullTextIndex (Id INT NOT NULL CONSTRAINT PK_NoFullTextIndex PRIMARY KEY, Body NVARCHAR(4000) NOT NULL);
        GO
        CREATE FULLTEXT CATALOG SemanticSearchCatalog AS DEFAULT;
        GO
        CREATE FULLTEXT INDEX ON dbo.PlainText(Body LANGUAGE 1033) KEY INDEX PK_PlainText;
        """;

    [Fact]
    public async Task NoFullTextIndexAtAll_KeyPhraseTableFailsWith41202()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync("SELECT * FROM SEMANTICKEYPHRASETABLE(dbo.NoFullTextIndex, Body, 1);"));

        Assert.Equal(41202, exception.Number);
    }

    [Fact]
    public async Task FullTextIndexWithoutStatisticalSemantics_KeyPhraseTableNamingTheColumnFailsWith41203()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync("SELECT * FROM SEMANTICKEYPHRASETABLE(dbo.PlainText, Body, 1);"));

        Assert.Equal(41203, exception.Number);
    }

    [Fact]
    public async Task FullTextIndexWithoutStatisticalSemantics_KeyPhraseTableWithStarFailsWith41202()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync("SELECT * FROM SEMANTICKEYPHRASETABLE(dbo.PlainText, *, 1);"));

        Assert.Equal(41202, exception.Number);
    }

    [Fact]
    public async Task LiveCatalogAndScanner_ReportTheOrdinaryFullTextIndexAsNotSemantic()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        Assert.True(catalog.Find("dbo.PlainText")!.HasFullTextIndex);
        Assert.Empty(catalog.Find("dbo.PlainText")!.SemanticFullTextColumnNames!);
        Assert.False(catalog.Find("dbo.NoFullTextIndex")!.HasFullTextIndex);
        Assert.Null(catalog.Find("dbo.NoFullTextIndex")!.SemanticFullTextColumnNames);

        var namedColumnResult = SqlScriptParser.ParseText("test.sql", "SELECT * FROM SEMANTICKEYPHRASETABLE(dbo.PlainText, Body, 1);");
        Assert.False(namedColumnResult.HasErrors, string.Join("; ", namedColumnResult.Errors.Select(e => e.Message)));
        var namedColumnFinding = Assert.Single(SemanticSearchScanner.Scan(namedColumnResult, catalog));
        Assert.Equal(SemanticSearchFindingKind.ColumnNotSemanticFullTextIndexed, namedColumnFinding.Kind);
        Assert.Equal("dbo.PlainText", namedColumnFinding.TableQualifiedName);
        Assert.Equal("Body", namedColumnFinding.ColumnName);

        var starResult = SqlScriptParser.ParseText("test.sql", "SELECT * FROM SEMANTICKEYPHRASETABLE(dbo.PlainText, *, 1);");
        Assert.False(starResult.HasErrors, string.Join("; ", starResult.Errors.Select(e => e.Message)));
        var starFinding = Assert.Single(SemanticSearchScanner.Scan(starResult, catalog));
        Assert.Equal(SemanticSearchFindingKind.TableNotSemanticFullTextIndexed, starFinding.Kind);
        Assert.Null(starFinding.ColumnName);

        var noIndexResult = SqlScriptParser.ParseText("test.sql", "SELECT * FROM SEMANTICKEYPHRASETABLE(dbo.NoFullTextIndex, Body, 1);");
        Assert.False(noIndexResult.HasErrors, string.Join("; ", noIndexResult.Errors.Select(e => e.Message)));
        var noIndexFinding = Assert.Single(SemanticSearchScanner.Scan(noIndexResult, catalog));
        Assert.Equal(SemanticSearchFindingKind.TableNotSemanticFullTextIndexed, noIndexFinding.Kind);
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
