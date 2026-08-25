using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class ProcCallArgumentMismatchMoneyOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(ProcCallArgumentMismatchMoneyOracleTests);

    private const string TakeMoneyInputProcedureText = """
        CREATE PROCEDURE dbo.TakeMoneyInput @Amount DECIMAL(4,1) AS
        BEGIN
            SELECT @Amount AS Got;
        END
        """;

    private const string ReturnMoneyOutputProcedureText = """
        CREATE PROCEDURE dbo.ReturnMoneyOutput @Amount MONEY OUTPUT AS
        BEGIN
            SET @Amount = 123.4567;
        END
        """;

    protected override string Ddl => $"""
        {TakeMoneyInputProcedureText}
        GO
        {ReturnMoneyOutputProcedureText}
        """;

    [Fact]
    public async Task MoneyCallerVariable_PassedIntoNarrowerDecimalParameter_EngineSilentlyRoundsIt_ScannerMustFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "DECLARE @CallerMoney MONEY = 123.4567; EXEC dbo.TakeMoneyInput @Amount = @CallerMoney;",
            connection);
        var engineResult = (decimal)(await command.ExecuteScalarAsync())!;
        Assert.Equal(123.5m, engineResult);

        var findings = ScanArgumentMismatch($"""
            {TakeMoneyInputProcedureText}
            GO
            DECLARE @CallerMoney MONEY = 123.4567;
            EXEC dbo.TakeMoneyInput @Amount = @CallerMoney;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.NumericScaleNarrowing, finding.Kind);
    }

    [Fact]
    public async Task MoneyOutputParameter_CopiedBackIntoNarrowerDecimalCallerVariable_EngineSilentlyRoundsIt_ScannerMustFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "DECLARE @CallerNarrow DECIMAL(10,2); EXEC dbo.ReturnMoneyOutput @Amount = @CallerNarrow OUTPUT; SELECT @CallerNarrow;",
            connection);
        var engineResult = (decimal)(await command.ExecuteScalarAsync())!;
        Assert.Equal(123.46m, engineResult);

        var findings = ScanArgumentMismatch($"""
            {ReturnMoneyOutputProcedureText}
            GO
            DECLARE @CallerNarrow DECIMAL(10,2);
            EXEC dbo.ReturnMoneyOutput @Amount = @CallerNarrow OUTPUT;
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
