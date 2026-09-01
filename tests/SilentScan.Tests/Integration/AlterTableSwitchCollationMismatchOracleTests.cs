using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;
using SilentScan.Verify.Catalog;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class AlterTableSwitchCollationMismatchOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(AlterTableSwitchCollationMismatchOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.MismatchSource (Id INT NOT NULL, Col VARCHAR(10) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
        GO
        CREATE TABLE dbo.MismatchTarget (Id INT NOT NULL, Col VARCHAR(10) COLLATE Latin1_General_CI_AS NOT NULL);
        GO
        CREATE TABLE dbo.MatchSource (Id INT NOT NULL, Col VARCHAR(10) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
        GO
        CREATE TABLE dbo.MatchTarget (Id INT NOT NULL, Col VARCHAR(10) NOT NULL);
        GO
        CREATE TABLE dbo.TypeMismatchSource (Id INT NOT NULL, Amount DECIMAL(10,2) NOT NULL);
        GO
        CREATE TABLE dbo.TypeMismatchTarget (Id INT NOT NULL, Amount DECIMAL(12,4) NOT NULL);
        """;

    [Fact]
    public async Task DifferentCollation_BlocksSwitchWith4945()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync("ALTER TABLE dbo.MismatchSource SWITCH TO dbo.MismatchTarget;"));

        Assert.Equal(4945, exception.Number);
        Assert.Contains("collation", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LiveCatalogAndScanner_ReportTheCollationMismatch()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        var result = SqlScriptParser.ParseText("test.sql", "ALTER TABLE dbo.MismatchSource SWITCH TO dbo.MismatchTarget;");

        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var finding = Assert.Single(QueryAntiPatternScanner.Scan(result, catalog), f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchColumnMismatch);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("4945", finding.DetailText);
    }

    [Fact]
    public async Task DeclaredCollationEquivalentToTargetDefault_SwitchSucceeds()
    {
        var exception = await Record.ExceptionAsync(() => ExecuteAsync("ALTER TABLE dbo.MatchSource SWITCH TO dbo.MatchTarget;"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task LiveCatalogAndScanner_DoesNotReportMismatchWhenCollationsAreEffectivelyEqual()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        var result = SqlScriptParser.ParseText("test.sql", "ALTER TABLE dbo.MatchSource SWITCH TO dbo.MatchTarget;");

        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.DoesNotContain(QueryAntiPatternScanner.Scan(result, catalog), f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchColumnMismatch);
    }

    [Fact]
    public async Task DifferentDataType_StillBlocksSwitchWith4944()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync("ALTER TABLE dbo.TypeMismatchSource SWITCH TO dbo.TypeMismatchTarget;"));

        Assert.Equal(4944, exception.Number);
    }

    [Fact]
    public async Task LiveCatalogAndScanner_StillReportsTheTypeMismatch()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        var result = SqlScriptParser.ParseText("test.sql", "ALTER TABLE dbo.TypeMismatchSource SWITCH TO dbo.TypeMismatchTarget;");

        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var finding = Assert.Single(QueryAntiPatternScanner.Scan(result, catalog), f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchColumnMismatch);
        Assert.Contains("4944", finding.DetailText);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
