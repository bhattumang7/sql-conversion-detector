using System.Globalization;
using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class ProcCallArgumentMismatchOutputParameterOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(ProcCallArgumentMismatchOutputParameterOracleTests);

    private const string ProcedureText = """
        CREATE PROCEDURE dbo.SetAmount @Amount DECIMAL(10,2) OUTPUT AS
        BEGIN
            SET @Amount = 123.4567;
        END
        """;

    private const string EchoAmountOnEntryProcedureText = """
        CREATE PROCEDURE dbo.EchoAmountOnEntry @Amount DECIMAL(4,1) OUTPUT AS
        BEGIN
            SELECT @Amount AS InitialAmount;
            SET @Amount = 0;
        END
        """;

    private const string BuildReferenceProcedureText = """
        CREATE PROCEDURE dbo.BuildReference @Reference VARCHAR(10) OUTPUT AS
        BEGIN
            SET @Reference = 'HelloWorld';
        END
        """;

    private const string SetTimestampProcedureText = """
        CREATE PROCEDURE dbo.SetTimestamp @Ts DATETIME2(7) OUTPUT AS
        BEGIN
            SET @Ts = '2024-03-15T10:20:30.1234567';
        END
        """;

    private const string SetOffsetTimestampProcedureText = """
        CREATE PROCEDURE dbo.SetOffsetTimestamp @Ts DATETIMEOFFSET(3) OUTPUT AS
        BEGIN
            SET @Ts = '2024-03-15 09:00:00.000 -05:00';
        END
        """;

    protected override string Ddl => $"""
        {ProcedureText}
        GO
        {EchoAmountOnEntryProcedureText}
        GO
        {BuildReferenceProcedureText}
        GO
        {SetTimestampProcedureText}
        GO
        {SetOffsetTimestampProcedureText}
        """;

    [Fact]
    public async Task OutputParameter_CopiedBackIntoWiderCallerVariable_EngineLosesNoData_ScannerMustNotFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "DECLARE @CallerWide DECIMAL(18,2) = 1.00; EXEC dbo.SetAmount @Amount = @CallerWide OUTPUT; SELECT @CallerWide;",
            connection);
        var engineResult = (decimal)(await command.ExecuteScalarAsync())!;
        Assert.Equal(123.46m, engineResult);

        var findings = ScanArgumentMismatch($"""
            {ProcedureText}
            GO
            DECLARE @CallerWide DECIMAL(18,2) = 1.00;
            EXEC dbo.SetAmount @Amount = @CallerWide OUTPUT;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task OutputParameter_CopiedBackIntoNarrowerCallerVariable_EngineSilentlyRoundsIt_ScannerMustFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "DECLARE @CallerNarrow DECIMAL(4,1); EXEC dbo.SetAmount @Amount = @CallerNarrow OUTPUT; SELECT @CallerNarrow;",
            connection);
        var engineResult = (decimal)(await command.ExecuteScalarAsync())!;
        Assert.Equal(123.5m, engineResult);

        var findings = ScanArgumentMismatch($"""
            {ProcedureText}
            GO
            DECLARE @CallerNarrow DECIMAL(4,1);
            EXEC dbo.SetAmount @Amount = @CallerNarrow OUTPUT;
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
            "DECLARE @CallerNarrow DECIMAL(4,1) = 1.0; EXEC dbo.SetAmount @Amount = @CallerNarrow; SELECT @CallerNarrow;",
            connection);
        var engineResult = (decimal)(await command.ExecuteScalarAsync())!;
        Assert.Equal(1.0m, engineResult);

        var findings = ScanArgumentMismatch($"""
            {ProcedureText}
            GO
            DECLARE @CallerNarrow DECIMAL(4,1) = 1.0;
            EXEC dbo.SetAmount @Amount = @CallerNarrow;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task OutputParameter_CallerValueCopiedInOnEntry_EngineSilentlyRoundsIt_ScannerMustFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "DECLARE @CallerWide DECIMAL(10,4) = 123.4567; EXEC dbo.EchoAmountOnEntry @Amount = @CallerWide OUTPUT;",
            connection);
        var engineResult = (decimal)(await command.ExecuteScalarAsync())!;
        Assert.Equal(123.5m, engineResult);

        var findings = ScanArgumentMismatch($"""
            {EchoAmountOnEntryProcedureText}
            GO
            DECLARE @CallerWide DECIMAL(10,4) = 123.4567;
            EXEC dbo.EchoAmountOnEntry @Amount = @CallerWide OUTPUT;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.NumericScaleNarrowing, finding.Kind);
        Assert.False(finding.IsOutputWriteback);
    }

    [Fact]
    public async Task OutputParameter_NeverAssignedWiderCallerVariable_EngineCopiesInNullNotData_ScannerMustNotFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "DECLARE @CallerWide DECIMAL(10,4); EXEC dbo.EchoAmountOnEntry @Amount = @CallerWide OUTPUT;",
            connection);
        var engineResult = await command.ExecuteScalarAsync();
        Assert.Equal(DBNull.Value, engineResult);

        var findings = ScanArgumentMismatch($"""
            {EchoAmountOnEntryProcedureText}
            GO
            DECLARE @CallerWide DECIMAL(10,4);
            EXEC dbo.EchoAmountOnEntry @Amount = @CallerWide OUTPUT;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task OutputParameter_CopiedBackIntoNarrowerVarcharCallerVariable_EngineSilentlyTruncatesIt_ScannerMustFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "DECLARE @CallerNarrow VARCHAR(3); EXEC dbo.BuildReference @Reference = @CallerNarrow OUTPUT; SELECT @CallerNarrow;",
            connection);
        var engineResult = (string)(await command.ExecuteScalarAsync())!;
        Assert.Equal("Hel", engineResult);

        var findings = ScanArgumentMismatch($"""
            {BuildReferenceProcedureText}
            GO
            DECLARE @CallerNarrow VARCHAR(3);
            EXEC dbo.BuildReference @Reference = @CallerNarrow OUTPUT;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.LengthTruncation, finding.Kind);
        Assert.True(finding.IsOutputWriteback);
    }

    [Fact]
    public async Task OutputParameter_CopiedBackIntoNarrowerScaleDateTime2CallerVariable_EngineSilentlyRoundsIt_ScannerMustFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "DECLARE @CallerNarrow DATETIME2(2); EXEC dbo.SetTimestamp @Ts = @CallerNarrow OUTPUT; SELECT @CallerNarrow;",
            connection);
        var engineResult = (DateTime)(await command.ExecuteScalarAsync())!;
        Assert.Equal(DateTime.Parse("2024-03-15T10:20:30.12", CultureInfo.InvariantCulture), engineResult);

        var findings = ScanArgumentMismatch($"""
            {SetTimestampProcedureText}
            GO
            DECLARE @CallerNarrow DATETIME2(2);
            EXEC dbo.SetTimestamp @Ts = @CallerNarrow OUTPUT;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.TemporalScaleNarrowing, finding.Kind);
        Assert.True(finding.IsOutputWriteback);
    }

    [Fact]
    public async Task OutputParameter_CopiedBackIntoOffsetUnawareCallerVariable_EngineSilentlyDropsTheOffset_ScannerMustFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "DECLARE @CallerNoOffset DATETIME2(3); EXEC dbo.SetOffsetTimestamp @Ts = @CallerNoOffset OUTPUT; SELECT @CallerNoOffset;",
            connection);
        var engineResult = (DateTime)(await command.ExecuteScalarAsync())!;
        Assert.Equal(DateTime.Parse("2024-03-15T09:00:00.000", CultureInfo.InvariantCulture), engineResult);

        var findings = ScanArgumentMismatch($"""
            {SetOffsetTimestampProcedureText}
            GO
            DECLARE @CallerNoOffset DATETIME2(3);
            EXEC dbo.SetOffsetTimestamp @Ts = @CallerNoOffset OUTPUT;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.TemporalOffsetDropped, finding.Kind);
        Assert.True(finding.IsOutputWriteback);
    }

    private static IReadOnlyList<ProcCallArgumentMismatchFinding> ScanArgumentMismatch(string sql)
    {
        var parsed = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(parsed.HasErrors, string.Join("; ", parsed.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([parsed]);
        var graph = ProcCallGraphBuilder.Build([parsed], catalog, new SkipLedger());
        return ProcCallArgumentMismatchScanner.Scan(graph);
    }
}
