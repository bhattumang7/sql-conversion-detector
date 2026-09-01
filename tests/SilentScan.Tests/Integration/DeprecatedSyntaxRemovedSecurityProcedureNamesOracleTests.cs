using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class DeprecatedSyntaxRemovedSecurityProcedureNamesOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(DeprecatedSyntaxRemovedSecurityProcedureNamesOracleTests);

    protected override string Ddl => string.Empty;

    private static IReadOnlyList<DeprecatedSyntaxFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return DeprecatedSyntaxScanner.Scan(result);
    }

    [Theory]
    [InlineData("sp_change_users_login")]
    [InlineData("sp_changedbowner")]
    public async Task RealServer_TracksProcedureAsDeprecatedFeature(string procedureName)
    {
        var cntrValue = await ExecuteScalarAsync(
            "SELECT cntr_value FROM sys.dm_os_performance_counters " +
            "WHERE object_name LIKE '%Deprecated%' AND instance_name = @name;",
            procedureName);

        Assert.NotEqual(DBNull.Value, cntrValue);
    }

    [Fact]
    public async Task RealServer_UntrackedProcedureName_NegativeControl_IsAbsentFromDeprecatedFeatureCounters()
    {
        var cntrValue = await ExecuteScalarAsync(
            "SELECT cntr_value FROM sys.dm_os_performance_counters " +
            "WHERE object_name LIKE '%Deprecated%' AND instance_name = @name;",
            "sp_this_name_is_not_engine_tracked");

        Assert.Equal(DBNull.Value, cntrValue);
    }

    [Fact]
    public void SpChangeUsersLogin_Fires()
    {
        var findings = Scan("EXEC sp_change_users_login 'auto_fix', 'someuser';");

        Assert.Contains(findings, f =>
            f.Kind == DeprecatedSyntaxFindingKind.RemovedSecurityStoredProcedure
            && f.DetailText.Contains("sp_change_users_login", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SpChangedbowner_Fires()
    {
        var findings = Scan("EXEC sp_changedbowner 'someuser';");

        Assert.Contains(findings, f =>
            f.Kind == DeprecatedSyntaxFindingKind.RemovedSecurityStoredProcedure
            && f.DetailText.Contains("sp_changedbowner", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MixedBatch_FlagsBothNewNamesAtTheirOwnStatementsOnly()
    {
        var findings = Scan(
            """
            EXEC sp_change_users_login 'auto_fix', 'someuser';
            EXEC dbo.spDoSomething;
            EXEC sp_changedbowner 'someuser';
            """);

        var securityFindings = findings
            .Where(f => f.Kind == DeprecatedSyntaxFindingKind.RemovedSecurityStoredProcedure)
            .ToList();

        Assert.Equal(2, securityFindings.Count);
        Assert.Contains(securityFindings, f => f.DetailText.Contains("sp_change_users_login", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(securityFindings, f => f.DetailText.Contains("sp_changedbowner", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(securityFindings, f => f.DetailText.Contains("spDoSomething", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<object> ExecuteScalarAsync(string sql, string parameterValue)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@name", parameterValue);
        return await command.ExecuteScalarAsync() ?? DBNull.Value;
    }
}
