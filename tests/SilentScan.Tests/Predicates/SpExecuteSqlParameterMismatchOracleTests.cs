using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class SpExecuteSqlParameterMismatchOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(SpExecuteSqlParameterMismatchOracleTests);

    protected override string Ddl => string.Empty;

    [Fact]
    public async Task InputParameter_NarrowerDeclaredVarcharParameter_EngineSilentlyTruncatesIt_ScannerMustFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            """
            DECLARE @sql NVARCHAR(MAX) = N'SELECT @SkuCode AS EchoedSku';
            DECLARE @sku VARCHAR(20) = 'WIDGET-CLEARANCE-24';
            EXEC sp_executesql @sql, N'@SkuCode VARCHAR(10)', @SkuCode = @sku;
            """,
            connection);
        var engineResult = (string)(await command.ExecuteScalarAsync())!;
        Assert.Equal("WIDGET-CLE", engineResult);

        var findings = ScanSpExecuteSqlMismatch("""
            DECLARE @sql NVARCHAR(MAX) = N'SELECT @SkuCode AS EchoedSku';
            DECLARE @sku VARCHAR(20) = 'WIDGET-CLEARANCE-24';
            EXEC sp_executesql @sql, N'@SkuCode VARCHAR(10)', @SkuCode = @sku;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.LengthTruncation, finding.Kind);
        Assert.False(finding.IsOutputWriteback);
    }

    [Fact]
    public async Task InputParameter_WidenDeclaredVarcharParameter_EngineLosesNoData_ScannerMustNotFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            """
            DECLARE @sql NVARCHAR(MAX) = N'SELECT @SkuCode AS EchoedSku';
            DECLARE @sku VARCHAR(20) = 'WIDGET-CLEARANCE-24';
            EXEC sp_executesql @sql, N'@SkuCode VARCHAR(20)', @SkuCode = @sku;
            """,
            connection);
        var engineResult = (string)(await command.ExecuteScalarAsync())!;
        Assert.Equal("WIDGET-CLEARANCE-24", engineResult);

        var findings = ScanSpExecuteSqlMismatch("""
            DECLARE @sql NVARCHAR(MAX) = N'SELECT @SkuCode AS EchoedSku';
            DECLARE @sku VARCHAR(20) = 'WIDGET-CLEARANCE-24';
            EXEC sp_executesql @sql, N'@SkuCode VARCHAR(20)', @SkuCode = @sku;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task OutputParameter_CopiedBackIntoNarrowerCallerVariable_EngineSilentlyRoundsIt_ScannerMustFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            """
            DECLARE @sql NVARCHAR(MAX) = N'SET @Tax = 12.3456';
            DECLARE @tax DECIMAL(4,1);
            EXEC sp_executesql @sql, N'@Tax DECIMAL(10,4) OUTPUT', @Tax = @tax OUTPUT;
            SELECT @tax;
            """,
            connection);
        var engineResult = (decimal)(await command.ExecuteScalarAsync())!;
        Assert.Equal(12.3m, engineResult);

        var findings = ScanSpExecuteSqlMismatch("""
            DECLARE @sql NVARCHAR(MAX) = N'SET @Tax = 12.3456';
            DECLARE @tax DECIMAL(4,1);
            EXEC sp_executesql @sql, N'@Tax DECIMAL(10,4) OUTPUT', @Tax = @tax OUTPUT;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.NumericScaleNarrowing, finding.Kind);
        Assert.True(finding.IsOutputWriteback);
    }

    [Fact]
    public async Task OutputParameter_CallSiteOmitsOutputKeyword_EngineNeverCopiesBack_ScannerMustNotFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            """
            DECLARE @sql NVARCHAR(MAX) = N'SET @Tax = 12.3456';
            DECLARE @tax DECIMAL(4,1) = 1.0;
            EXEC sp_executesql @sql, N'@Tax DECIMAL(10,4) OUTPUT', @Tax = @tax;
            SELECT @tax;
            """,
            connection);
        var engineResult = (decimal)(await command.ExecuteScalarAsync())!;
        Assert.Equal(1.0m, engineResult);

        var findings = ScanSpExecuteSqlMismatch("""
            DECLARE @sql NVARCHAR(MAX) = N'SET @Tax = 12.3456';
            DECLARE @tax DECIMAL(4,1) = 1.0;
            EXEC sp_executesql @sql, N'@Tax DECIMAL(10,4) OUTPUT', @Tax = @tax;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task InputParameter_PositionalCall_NarrowerDeclaredVarcharParameter_EngineSilentlyTruncatesIt_ScannerMustFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            """
            DECLARE @sql NVARCHAR(MAX) = N'SELECT @SkuCode AS EchoedSku';
            DECLARE @sku VARCHAR(20) = 'WIDGET-CLEARANCE-24';
            EXEC sp_executesql @sql, N'@SkuCode VARCHAR(10)', @sku;
            """,
            connection);
        var engineResult = (string)(await command.ExecuteScalarAsync())!;
        Assert.Equal("WIDGET-CLE", engineResult);

        var findings = ScanSpExecuteSqlMismatch("""
            DECLARE @sql NVARCHAR(MAX) = N'SELECT @SkuCode AS EchoedSku';
            DECLARE @sku VARCHAR(20) = 'WIDGET-CLEARANCE-24';
            EXEC sp_executesql @sql, N'@SkuCode VARCHAR(10)', @sku;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.LengthTruncation, finding.Kind);
        Assert.False(finding.IsOutputWriteback);
    }

    [Fact]
    public async Task InputParameter_PositionalCall_SameWidthDeclaredVarcharParameter_EngineLosesNoData_ScannerMustNotFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            """
            DECLARE @sql NVARCHAR(MAX) = N'SELECT @SkuCode AS EchoedSku';
            DECLARE @sku VARCHAR(20) = 'WIDGET-CLEARANCE-24';
            EXEC sp_executesql @sql, N'@SkuCode VARCHAR(20)', @sku;
            """,
            connection);
        var engineResult = (string)(await command.ExecuteScalarAsync())!;
        Assert.Equal("WIDGET-CLEARANCE-24", engineResult);

        var findings = ScanSpExecuteSqlMismatch("""
            DECLARE @sql NVARCHAR(MAX) = N'SELECT @SkuCode AS EchoedSku';
            DECLARE @sku VARCHAR(20) = 'WIDGET-CLEARANCE-24';
            EXEC sp_executesql @sql, N'@SkuCode VARCHAR(20)', @sku;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task MixedPositionalThenNamedCall_FlagsOnlyTheNarrowedParameter()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            """
            DECLARE @sql NVARCHAR(MAX) = N'SELECT @SkuCode AS EchoedSku; SET @Tax = 12.3456';
            DECLARE @sku VARCHAR(20) = 'WIDGET-CLEARANCE-24';
            DECLARE @tax DECIMAL(10,4);
            EXEC sp_executesql @sql, N'@SkuCode VARCHAR(10), @Tax DECIMAL(10,4) OUTPUT', @sku, @Tax = @tax OUTPUT;
            SELECT @tax AS EchoedTax;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("WIDGET-CLE", reader.GetString(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(12.3456m, reader.GetDecimal(0));

        var findings = ScanSpExecuteSqlMismatch("""
            DECLARE @sql NVARCHAR(MAX) = N'SELECT @SkuCode; SET @Tax = 12.3456';
            DECLARE @sku VARCHAR(20) = 'WIDGET-CLEARANCE-24';
            DECLARE @tax DECIMAL(10,4);
            EXEC sp_executesql @sql, N'@SkuCode VARCHAR(10), @Tax DECIMAL(10,4) OUTPUT', @sku, @Tax = @tax OUTPUT;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.LengthTruncation, finding.Kind);
        Assert.False(finding.IsOutputWriteback);
    }

    private static IReadOnlyList<SpExecuteSqlParameterMismatchFinding> ScanSpExecuteSqlMismatch(string sql)
    {
        var parsed = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(parsed.HasErrors, string.Join("; ", parsed.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([parsed]);
        var graph = ProcCallGraphBuilder.Build([parsed], catalog, new SkipLedger());
        return SpExecuteSqlParameterMismatchScanner.Scan(graph);
    }
}
