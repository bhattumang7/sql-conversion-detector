using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second OSS/commercial sweep": "Output parameter not populated on
/// every code path" - oracle-confirms the general mechanism once (not per finding, matching this
/// codebase's own precedent, e.g. <see cref="TransactionHygieneOracleTests"/>): a real executed
/// procedure whose OUTPUT parameter is never assigned on the path taken leaves the CALLING
/// session's own variable completely UNCHANGED - not reset to NULL, not defaulted, literally
/// untouched regardless of what it held before the call.
/// </summary>
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
}
