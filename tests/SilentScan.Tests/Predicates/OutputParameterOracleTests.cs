using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class OutputParameterOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(OutputParameterOracleTests);

    protected override string Ddl => """
        CREATE PROCEDURE dbo.p_never_assigns @x INT OUTPUT AS
        BEGIN
            SET NOCOUNT ON;
            IF (1 = 0) SET @x = 42;
        END
        GO
        CREATE PROCEDURE dbo.p_always_assigns @x INT OUTPUT AS
        BEGIN
            SET NOCOUNT ON;
            SET @x = 42;
        END
        GO
        CREATE PROCEDURE dbo.p_conditional_select_assigns @x INT OUTPUT AS
        BEGIN
            SET NOCOUNT ON;
            DECLARE @T TABLE (Id INT, Val INT);
            SELECT @x = Val FROM @T WHERE Id = 1;
        END
        GO
        CREATE PROCEDURE dbo.p_conditional_aggregate_assigns @x INT OUTPUT AS
        BEGIN
            SET NOCOUNT ON;
            DECLARE @T TABLE (Id INT, Val INT);
            SELECT @x = SUM(Val) FROM @T WHERE Id = 1;
        END
        GO
        """;

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        return connection;
    }

    [Fact]
    public async Task NeverAssignedOutputParameter_LeavesCallerVariableAtItsPriorRealValue_NotNull()
    {
        await using var connection = await OpenConnectionAsync();

        await using var command = new SqlCommand(
            "DECLARE @caller INT = 999; EXEC dbo.p_never_assigns @x = @caller OUTPUT; SELECT @caller;",
            connection);
        var result = await command.ExecuteScalarAsync();

        Assert.Equal(999, (int)result!);
    }

    [Fact]
    public async Task NeverAssignedOutputParameter_LeavesCallerVariableNull_WhenItStartedNull()
    {
        await using var connection = await OpenConnectionAsync();

        await using var command = new SqlCommand(
            "DECLARE @caller INT = NULL; EXEC dbo.p_never_assigns @x = @caller OUTPUT; SELECT @caller;",
            connection);
        var result = await command.ExecuteScalarAsync();

        Assert.True(result is null or DBNull);
    }

    [Fact]
    public async Task AlwaysAssignedOutputParameter_OverwritesWhateverTheCallerHeldBefore()
    {
        await using var connection = await OpenConnectionAsync();

        await using var command = new SqlCommand(
            "DECLARE @caller INT = 999; EXEC dbo.p_always_assigns @x = @caller OUTPUT; SELECT @caller;",
            connection);
        var result = await command.ExecuteScalarAsync();

        Assert.Equal(42, (int)result!);
    }

    [Fact]
    public async Task ConditionalNonAggregateSelectAssignment_ZeroMatchingRows_LeavesCallerVariableAtItsPriorRealValue_AndScannerNowFlagsSoleAssignment()
    {
        await using var connection = await OpenConnectionAsync();

        await using var command = new SqlCommand(
            "DECLARE @caller INT = 42; EXEC dbo.p_conditional_select_assigns @x = @caller OUTPUT; SELECT @caller;",
            connection);
        var result = await command.ExecuteScalarAsync();

        Assert.Equal(42, (int)result!);

        var findings = Scan(
            """
            DECLARE @T TABLE (Id INT, Val INT);
            SELECT @x = Val FROM @T WHERE Id = 1;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("@x", finding.ParameterName);
    }

    [Fact]
    public async Task ConditionalAggregateSelectAssignment_ZeroMatchingRows_StillAssignsNull_AndScannerStillDoesNotFlagSoleAssignment()
    {
        await using var connection = await OpenConnectionAsync();

        await using var command = new SqlCommand(
            "DECLARE @caller INT = 42; EXEC dbo.p_conditional_aggregate_assigns @x = @caller OUTPUT; SELECT @caller;",
            connection);
        var result = await command.ExecuteScalarAsync();

        Assert.True(result is null or DBNull);

        var findings = Scan(
            """
            DECLARE @T TABLE (Id INT, Val INT);
            SELECT @x = SUM(Val) FROM @T WHERE Id = 1;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task NoFromClauseSelectSetVariable_AlwaysAssigns_AndScannerStillDoesNotFlagSoleAssignment()
    {
        await using var connection = await OpenConnectionAsync();

        await using var command = new SqlCommand(
            "DECLARE @caller INT = 999; SELECT @caller = 42; SELECT @caller;",
            connection);
        var result = await command.ExecuteScalarAsync();

        Assert.Equal(42, (int)result!);

        var findings = Scan("SELECT @x = 42;");

        Assert.Empty(findings);
    }

    private static IReadOnlyList<OutputParameterFinding> Scan(string procedureBody)
    {
        var sql = $"CREATE PROCEDURE dbo.p @x INT OUTPUT AS\nBEGIN\n{procedureBody}\nEND";
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return OutputParameterScanner.Scan(result);
    }
}
