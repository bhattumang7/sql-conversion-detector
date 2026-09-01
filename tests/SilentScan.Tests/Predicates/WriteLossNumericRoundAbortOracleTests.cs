using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class WriteLossNumericRoundAbortOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(WriteLossNumericRoundAbortOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.T (
            Id INT IDENTITY PRIMARY KEY,
            DecCol DECIMAL(10,2) NULL
        );
        """;

    [Fact]
    public async Task DeclareVariableInitializer_DecimalScaleNarrowing_DefaultRoundAbortOff_SilentlyRounds_ScannerFlags()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand("DECLARE @d DECIMAL(5,2) = 123.456; SELECT @d;", connection);
        var result = await command.ExecuteScalarAsync();
        Assert.Equal(123.46m, result);

        var findings = Extract("DECLARE @d DECIMAL(5,2) = 123.456;");
        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.NumericScaleNarrowing, finding.Kind);
        Assert.Equal("@d", finding.ColumnName);
    }

    [Fact]
    public async Task NumericRoundAbortOn_DecimalToDecimalScaleNarrowing_RaisesHardError_NotSilent()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "SET NUMERIC_ROUNDABORT ON; DECLARE @d DECIMAL(5,2) = 123.456; SELECT @d;",
            connection);
        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());
        Assert.Equal(8115, exception.Number);

        var findingsWithRoundAbortOn = Extract(
            "DECLARE @d DECIMAL(5,2); SET NUMERIC_ROUNDABORT ON; SET @d = 123.456;");
        Assert.Empty(findingsWithRoundAbortOn);

        var findingsWithoutRoundAbort = Extract("DECLARE @d DECIMAL(5,2) = 123.456;");
        var finding = Assert.Single(findingsWithoutRoundAbort);
        Assert.Equal(WriteLossKind.NumericScaleNarrowing, finding.Kind);
    }

    [Fact]
    public async Task NumericRoundAbortOn_IntTruncation_StillSilent_ScannerStillFlags()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "SET NUMERIC_ROUNDABORT ON; DECLARE @i INT = 7.9; SELECT @i;",
            connection);
        var result = await command.ExecuteScalarAsync();
        Assert.Equal(7, result);

        var findings = Extract("DECLARE @i INT; SET NUMERIC_ROUNDABORT ON; SET @i = 7.9;");
        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.NumericScaleNarrowing, finding.Kind);
    }

    [Fact]
    public async Task NumericRoundAbortOn_VarcharTruncation_StillSilent_ScannerStillFlags()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "SET NUMERIC_ROUNDABORT ON; DECLARE @v VARCHAR(5) = 'abcdef'; SELECT @v;",
            connection);
        var result = await command.ExecuteScalarAsync();
        Assert.Equal("abcde", result);

        var findings = Extract("DECLARE @v VARCHAR(5); SET NUMERIC_ROUNDABORT ON; SET @v = 'abcdef';");
        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.LengthTruncation, finding.Kind);
    }

    [Fact]
    public async Task NumericRoundAbortOn_FloatToDecimalNarrowing_StillSilent_ScannerStillFlags()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "SET NUMERIC_ROUNDABORT ON; DECLARE @f FLOAT = 7.9; DECLARE @d2 DECIMAL(5,0); SET @d2 = @f; SELECT @d2;",
            connection);
        var result = await command.ExecuteScalarAsync();
        Assert.Equal(8m, result);

        var findings = Extract(
            "DECLARE @f FLOAT = 7.9; DECLARE @d2 DECIMAL(5,0); SET NUMERIC_ROUNDABORT ON; SET @d2 = @f;");
        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.ApproximateToExactTruncation, finding.Kind);
    }

    [Fact]
    public async Task NumericRoundAbortOn_InsertIntoTableColumn_TerminatesStatement_ScannerSuppressesFinding()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "SET NUMERIC_ROUNDABORT ON; INSERT INTO dbo.T (DecCol) VALUES (123.456);",
            connection);
        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(8115, exception.Number);

        await using var countCommand = new SqlCommand("SELECT COUNT(*) FROM dbo.T", connection);
        Assert.Equal(0, (int)(await countCommand.ExecuteScalarAsync())!);

        var findings = ExtractTable(
            """
            CREATE TABLE dbo.T (DecCol DECIMAL(10,2) NULL);
            """,
            "SET NUMERIC_ROUNDABORT ON; INSERT INTO dbo.T (DecCol) VALUES (123.456);");
        Assert.Empty(findings);
    }

    private static IReadOnlyList<WriteLossFinding> Extract(string variableScript) => ExtractSql(variableScript);

    private static IReadOnlyList<WriteLossFinding> ExtractTable(string ddl, string statement) =>
        ExtractSql($"{ddl}\nGO\n{statement}");

    private static IReadOnlyList<WriteLossFinding> ExtractSql(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        return TypedPredicateExtractor.Extract(result, catalog, lineage).WriteLossFindings;
    }
}
