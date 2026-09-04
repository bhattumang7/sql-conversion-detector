using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class DeprecatedSyntaxLegacyLobOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(DeprecatedSyntaxLegacyLobOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Ticket (Id INT NOT NULL PRIMARY KEY, Notes TEXT NULL);
        INSERT INTO dbo.Ticket (Id, Notes) VALUES (1, 'hello world');
        """;

    private static IReadOnlyList<DeprecatedSyntaxFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return DeprecatedSyntaxScanner.Scan(result);
    }

    [Fact]
    public async Task RealServer_ReadText_IncrementsItsOwnDeprecatedFeatureCounter()
    {
        var before = await ReadCounterAsync("READTEXT");

        await ExecuteNonQueryAsync("""
            DECLARE @ptr VARBINARY(16);
            SELECT @ptr = TEXTPTR(Notes) FROM dbo.Ticket WHERE Id = 1;
            READTEXT dbo.Ticket.Notes @ptr 0 5;
            """);

        var after = await ReadCounterAsync("READTEXT");
        Assert.True(after > before, $"expected READTEXT counter to increase, before={before} after={after}");
    }

    [Theory]
    [InlineData("""
        DECLARE @ptr VARBINARY(16);
        SELECT @ptr = TEXTPTR(Notes) FROM dbo.Ticket WHERE Id = 1;
        WRITETEXT dbo.Ticket.Notes @ptr 'replaced';
        """)]
    [InlineData("""
        DECLARE @ptr VARBINARY(16);
        SELECT @ptr = TEXTPTR(Notes) FROM dbo.Ticket WHERE Id = 1;
        UPDATETEXT dbo.Ticket.Notes @ptr 0 5 'HELLO';
        """)]
    public async Task RealServer_WriteTextOrUpdateText_IncrementsSharedDeprecatedFeatureCounter(string statement)
    {
        var before = await ReadCounterAsync("UPDATETEXT or WRITETEXT");

        await ExecuteNonQueryAsync(statement);

        var after = await ReadCounterAsync("UPDATETEXT or WRITETEXT");
        Assert.True(after > before, $"expected 'UPDATETEXT or WRITETEXT' counter to increase, before={before} after={after}");
    }

    [Fact]
    public async Task RealServer_TextPtr_IncrementsItsOwnDeprecatedFeatureCounter()
    {
        var before = await ReadCounterAsync("TEXTPTR");

        await ExecuteNonQueryAsync("""
            DECLARE @ptr VARBINARY(16);
            SELECT @ptr = TEXTPTR(Notes) FROM dbo.Ticket WHERE Id = 1;
            """);

        var after = await ReadCounterAsync("TEXTPTR");
        Assert.True(after > before, $"expected TEXTPTR counter to increase, before={before} after={after}");
    }

    [Fact]
    public async Task RealServer_TextValid_IncrementsItsOwnDeprecatedFeatureCounter()
    {
        var before = await ReadCounterAsync("TEXTVALID");

        await ExecuteNonQueryAsync("""
            DECLARE @ptr VARBINARY(16);
            SELECT @ptr = TEXTPTR(Notes) FROM dbo.Ticket WHERE Id = 1;
            DECLARE @valid INT = TEXTVALID('dbo.Ticket.Notes', @ptr);
            """);

        var after = await ReadCounterAsync("TEXTVALID");
        Assert.True(after > before, $"expected TEXTVALID counter to increase, before={before} after={after}");
    }

    [Fact]
    public void ReadTextStatement_Fires()
    {
        var findings = Scan("DECLARE @ptr VARBINARY(16); READTEXT dbo.Ticket.Notes @ptr 0 5;");

        Assert.Contains(findings, f =>
            f.Kind == DeprecatedSyntaxFindingKind.LegacyLobStatement
            && f.DetailText.Contains("READTEXT", StringComparison.Ordinal)
            && f.DetailText.Contains("dbo.Ticket.Notes", StringComparison.Ordinal));
    }

    [Fact]
    public void WriteTextStatement_Fires()
    {
        var findings = Scan("DECLARE @ptr VARBINARY(16); WRITETEXT dbo.Ticket.Notes @ptr 'x';");

        Assert.Contains(findings, f =>
            f.Kind == DeprecatedSyntaxFindingKind.LegacyLobStatement
            && f.DetailText.Contains("WRITETEXT", StringComparison.Ordinal)
            && f.DetailText.Contains("dbo.Ticket.Notes", StringComparison.Ordinal));
    }

    [Fact]
    public void UpdateTextStatement_Fires()
    {
        var findings = Scan("DECLARE @ptr VARBINARY(16); UPDATETEXT dbo.Ticket.Notes @ptr 0 5 'HELLO';");

        Assert.Contains(findings, f =>
            f.Kind == DeprecatedSyntaxFindingKind.LegacyLobStatement
            && f.DetailText.Contains("UPDATETEXT", StringComparison.Ordinal)
            && f.DetailText.Contains("dbo.Ticket.Notes", StringComparison.Ordinal));
    }

    [Fact]
    public void TextPtrFunction_Fires()
    {
        var findings = Scan("SELECT TEXTPTR(Notes) FROM dbo.Ticket;");

        Assert.Contains(findings, f =>
            f.Kind == DeprecatedSyntaxFindingKind.LegacyLobFunction
            && f.DetailText.Contains("TEXTPTR", StringComparison.Ordinal));
    }

    [Fact]
    public void TextValidFunction_Fires()
    {
        var findings = Scan("DECLARE @ptr VARBINARY(16); SELECT TEXTVALID('dbo.Ticket.Notes', @ptr);");

        Assert.Contains(findings, f =>
            f.Kind == DeprecatedSyntaxFindingKind.LegacyLobFunction
            && f.DetailText.Contains("TEXTVALID", StringComparison.Ordinal));
    }

    [Fact]
    public void SchemaQualifiedUserDefinedFunctionNamedTextPtr_NegativeControl_NeverFires()
    {
        var findings = Scan("SELECT dbo.TextPtr(1);");

        Assert.DoesNotContain(findings, f => f.Kind == DeprecatedSyntaxFindingKind.LegacyLobFunction);
    }

    [Fact]
    public void MixedBatch_OnlyFlagsLobStatementsAndFunctions_NotOrdinaryStatements()
    {
        var findings = Scan(
            """
            DECLARE @ptr VARBINARY(16);
            SELECT @ptr = TEXTPTR(Notes) FROM dbo.Ticket WHERE Id = 1;
            UPDATE dbo.Ticket SET Id = Id;
            READTEXT dbo.Ticket.Notes @ptr 0 5;
            SELECT SUBSTRING(Notes, 1, 5) FROM dbo.Ticket;
            """);

        var lobFindings = findings
            .Where(f => f.Kind is DeprecatedSyntaxFindingKind.LegacyLobStatement or DeprecatedSyntaxFindingKind.LegacyLobFunction)
            .ToList();

        Assert.Equal(2, lobFindings.Count);
        Assert.Contains(lobFindings, f => f.Kind == DeprecatedSyntaxFindingKind.LegacyLobFunction && f.DetailText.Contains("TEXTPTR", StringComparison.Ordinal));
        Assert.Contains(lobFindings, f => f.Kind == DeprecatedSyntaxFindingKind.LegacyLobStatement && f.DetailText.Contains("READTEXT", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("TEXT")]
    [InlineData("NTEXT")]
    [InlineData("IMAGE")]
    public async Task RealServer_LocalVariableDeclaredWithLegacyLobType_FailsWithMsg2739_AndScannerFlagsIt(string typeName)
    {
        var exception = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteNonQueryAsync($"DECLARE @x {typeName}; SELECT @x;"));

        Assert.Equal(2739, exception.Number);

        var findings = Scan($"DECLARE @x {typeName}; SELECT @x;");
        Assert.Contains(findings, f => f.Kind == DeprecatedSyntaxFindingKind.LegacyLobLocalVariable);
    }

    [Fact]
    public async Task RealServer_ProcedureParameterDeclaredAsText_Succeeds_AndScannerDoesNotFlagIt()
    {
        var exception = await Record.ExceptionAsync(
            () => ExecuteNonQueryAsync("CREATE OR ALTER PROCEDURE dbo.TakesLobParam @x TEXT AS SELECT @x;"));

        Assert.Null(exception);

        var findings = Scan("CREATE PROCEDURE dbo.TakesLobParam @x TEXT AS SELECT @x;");
        Assert.DoesNotContain(findings, f => f.Kind == DeprecatedSyntaxFindingKind.LegacyLobLocalVariable);
    }

    private async Task<long> ReadCounterAsync(string instanceName)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT cntr_value FROM sys.dm_os_performance_counters WHERE object_name LIKE '%Deprecated%' AND instance_name = @name;";
        command.Parameters.AddWithValue("@name", instanceName);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? 0L : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ExecuteNonQueryAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
