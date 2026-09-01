using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class WriteLossFetchIntoOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(WriteLossFetchIntoOracleTests);

    protected override string Ddl => string.Empty;

    [Fact]
    public async Task FetchInto_DecimalScaleNarrowing_SilentlyRounds_ScannerFlagsTheNarrowedPosition()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            """
            DECLARE @ok INT;
            DECLARE @d DECIMAL(5,2);
            DECLARE cur CURSOR LOCAL FOR SELECT 5, CAST(123.456 AS DECIMAL(10,4));
            OPEN cur;
            FETCH NEXT FROM cur INTO @ok, @d;
            SELECT @ok, @d;
            CLOSE cur;
            DEALLOCATE cur;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(5, reader.GetInt32(0));
        Assert.Equal(123.46m, reader.GetDecimal(1));

        var findings = Extract(
            """
            DECLARE @ok INT;
            DECLARE @d DECIMAL(5,2);
            DECLARE cur CURSOR LOCAL FOR SELECT 5, CAST(123.456 AS DECIMAL(10,4));
            OPEN cur;
            FETCH NEXT FROM cur INTO @ok, @d;
            CLOSE cur;
            DEALLOCATE cur;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("@d", finding.ColumnName);
        Assert.Null(finding.TableQualifiedName);
        Assert.Equal(WriteLossKind.NumericScaleNarrowing, finding.Kind);
    }

    [Fact]
    public async Task FetchInto_VarcharLengthTruncation_SilentlyTruncates_ScannerFlags()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            """
            DECLARE @v VARCHAR(5);
            DECLARE cur CURSOR LOCAL FOR SELECT CAST('abcdef' AS VARCHAR(10));
            OPEN cur;
            FETCH NEXT FROM cur INTO @v;
            SELECT @v;
            CLOSE cur;
            DEALLOCATE cur;
            """,
            connection);
        var result = await command.ExecuteScalarAsync();
        Assert.Equal("abcde", result);

        var findings = Extract(
            """
            DECLARE @v VARCHAR(5);
            DECLARE cur CURSOR LOCAL FOR SELECT CAST('abcdef' AS VARCHAR(10));
            OPEN cur;
            FETCH NEXT FROM cur INTO @v;
            CLOSE cur;
            DEALLOCATE cur;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("@v", finding.ColumnName);
        Assert.Equal(WriteLossKind.LengthTruncation, finding.Kind);
    }

    [Fact]
    public async Task FetchInto_MatchingTypes_NoTruncation_ScannerReportsNothing()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            """
            DECLARE @v VARCHAR(10);
            DECLARE cur CURSOR LOCAL FOR SELECT CAST('abcde' AS VARCHAR(10));
            OPEN cur;
            FETCH NEXT FROM cur INTO @v;
            SELECT @v;
            CLOSE cur;
            DEALLOCATE cur;
            """,
            connection);
        var result = await command.ExecuteScalarAsync();
        Assert.Equal("abcde", result);

        var findings = Extract(
            """
            DECLARE @v VARCHAR(10);
            DECLARE cur CURSOR LOCAL FOR SELECT CAST('abcde' AS VARCHAR(10));
            OPEN cur;
            FETCH NEXT FROM cur INTO @v;
            CLOSE cur;
            DEALLOCATE cur;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task FetchInto_NumericRoundAbortOn_DecimalNarrowing_HardErrors_ScannerSuppressesFinding()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            """
            SET NUMERIC_ROUNDABORT ON;
            DECLARE @d DECIMAL(5,2);
            DECLARE cur CURSOR LOCAL FOR SELECT CAST(123.456 AS DECIMAL(10,4));
            OPEN cur;
            FETCH NEXT FROM cur INTO @d;
            CLOSE cur;
            DEALLOCATE cur;
            """,
            connection);
        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(8115, exception.Number);

        var findings = Extract(
            """
            DECLARE @d DECIMAL(5,2);
            SET NUMERIC_ROUNDABORT ON;
            DECLARE cur CURSOR LOCAL FOR SELECT CAST(123.456 AS DECIMAL(10,4));
            OPEN cur;
            FETCH NEXT FROM cur INTO @d;
            CLOSE cur;
            DEALLOCATE cur;
            """);

        Assert.Empty(findings);
    }

    private static IReadOnlyList<WriteLossFinding> Extract(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        return TypedPredicateExtractor.Extract(result, catalog, lineage).WriteLossFindings;
    }
}
