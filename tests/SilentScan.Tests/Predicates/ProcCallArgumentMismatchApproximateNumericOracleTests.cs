using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class ProcCallArgumentMismatchApproximateNumericOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(ProcCallArgumentMismatchApproximateNumericOracleTests);

    private const string RoundTripApproximateInputProcedureText = """
        CREATE PROCEDURE dbo.RoundTripApproximateInput @Value REAL, @Echo FLOAT OUTPUT AS
        BEGIN
            SET @Echo = @Value;
        END
        """;

    protected override string Ddl => RoundTripApproximateInputProcedureText;

    [Fact]
    public async Task FloatCallerVariable_PassedIntoNarrowerRealParameter_EngineSilentlyRoundsIt_ScannerMustFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            """
            DECLARE @CallerFloat FLOAT = SQRT(2.0);
            DECLARE @Echo FLOAT;
            EXEC dbo.RoundTripApproximateInput @Value = @CallerFloat, @Echo = @Echo OUTPUT;
            SELECT CASE WHEN @Echo = @CallerFloat THEN 0 ELSE 1 END;
            """,
            connection);
        var engineResult = (int)(await command.ExecuteScalarAsync())!;
        Assert.Equal(1, engineResult);

        var findings = ScanArgumentMismatch($"""
            {RoundTripApproximateInputProcedureText}
            GO
            DECLARE @CallerFloat FLOAT;
            DECLARE @Echo FLOAT;
            EXEC dbo.RoundTripApproximateInput @Value = @CallerFloat, @Echo = @Echo OUTPUT;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("@Value", finding.FormalParameterName);
        Assert.Equal(WriteLossKind.NumericScaleNarrowing, finding.Kind);
        Assert.False(finding.IsOutputWriteback);
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
