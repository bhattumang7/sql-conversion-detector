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

    protected override string Ddl => ProcedureText;

    [Fact]
    public async Task OutputParameter_CopiedBackIntoWiderCallerVariable_EngineLosesNoData_ScannerMustNotFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "DECLARE @CallerWide DECIMAL(10,4); EXEC dbo.SetAmount @Amount = @CallerWide OUTPUT; SELECT @CallerWide;",
            connection);
        var engineResult = (decimal)(await command.ExecuteScalarAsync())!;
        Assert.Equal(123.4600m, engineResult);

        var findings = ScanArgumentMismatch($"""
            {ProcedureText}
            GO
            DECLARE @CallerWide DECIMAL(10,4);
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
