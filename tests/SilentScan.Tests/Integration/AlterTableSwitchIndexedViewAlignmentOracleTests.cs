using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;
using SilentScan.Verify.Catalog;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class AlterTableSwitchIndexedViewAlignmentOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(AlterTableSwitchIndexedViewAlignmentOracleTests);

    protected override string Ddl => """
        CREATE PARTITION FUNCTION PfCountMismatch (int) AS RANGE LEFT FOR VALUES (10, 20, 30);
        GO
        CREATE PARTITION SCHEME PsCountMismatch AS PARTITION PfCountMismatch ALL TO ([PRIMARY]);
        GO
        CREATE TABLE dbo.CountMismatchSource (Id INT NOT NULL, Grp INT NOT NULL, Val INT NOT NULL) ON PsCountMismatch(Grp);
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_CountMismatchSource ON dbo.CountMismatchSource(Grp, Id) ON PsCountMismatch(Grp);
        GO
        CREATE TABLE dbo.CountMismatchTarget (Id INT NOT NULL, Grp INT NOT NULL, Val INT NOT NULL) ON PsCountMismatch(Grp);
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_CountMismatchTarget ON dbo.CountMismatchTarget(Grp, Id) ON PsCountMismatch(Grp);
        GO
        CREATE VIEW dbo.CountMismatchTargetView WITH SCHEMABINDING AS
        SELECT Grp, Id, Val FROM dbo.CountMismatchTarget;
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_CountMismatchTargetView ON dbo.CountMismatchTargetView(Grp, Id) ON PsCountMismatch(Grp);
        GO

        CREATE PARTITION FUNCTION PfMatched (int) AS RANGE LEFT FOR VALUES (10, 20, 30);
        GO
        CREATE PARTITION SCHEME PsMatched AS PARTITION PfMatched ALL TO ([PRIMARY]);
        GO
        CREATE TABLE dbo.MatchedSource (Id INT NOT NULL, Grp INT NOT NULL, Val INT NOT NULL) ON PsMatched(Grp);
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_MatchedSource ON dbo.MatchedSource(Grp, Id) ON PsMatched(Grp);
        GO
        CREATE TABLE dbo.MatchedTarget (Id INT NOT NULL, Grp INT NOT NULL, Val INT NOT NULL) ON PsMatched(Grp);
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_MatchedTarget ON dbo.MatchedTarget(Grp, Id) ON PsMatched(Grp);
        GO
        CREATE VIEW dbo.MatchedSourceView WITH SCHEMABINDING AS
        SELECT Grp, Id, Val FROM dbo.MatchedSource;
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_MatchedSourceView ON dbo.MatchedSourceView(Grp, Id) ON PsMatched(Grp);
        GO
        CREATE VIEW dbo.MatchedTargetView WITH SCHEMABINDING AS
        SELECT Grp, Id, Val FROM dbo.MatchedTarget;
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_MatchedTargetView ON dbo.MatchedTargetView(Grp, Id) ON PsMatched(Grp);
        GO

        CREATE PARTITION FUNCTION PfUnpartitionedView (int) AS RANGE LEFT FOR VALUES (10, 20, 30);
        GO
        CREATE PARTITION SCHEME PsUnpartitionedView AS PARTITION PfUnpartitionedView ALL TO ([PRIMARY]);
        GO
        CREATE TABLE dbo.UnpartitionedViewSource (Id INT NOT NULL, Grp INT NOT NULL, Val INT NOT NULL) ON PsUnpartitionedView(Grp);
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_UnpartitionedViewSource ON dbo.UnpartitionedViewSource(Grp, Id) ON PsUnpartitionedView(Grp);
        GO
        CREATE TABLE dbo.UnpartitionedViewTarget (Id INT NOT NULL, Grp INT NOT NULL, Val INT NOT NULL) ON PsUnpartitionedView(Grp);
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_UnpartitionedViewTarget ON dbo.UnpartitionedViewTarget(Grp, Id) ON PsUnpartitionedView(Grp);
        GO
        CREATE VIEW dbo.UnpartitionedSourceView WITH SCHEMABINDING AS
        SELECT Grp, Id, Val FROM dbo.UnpartitionedViewSource;
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_UnpartitionedSourceView ON dbo.UnpartitionedSourceView(Grp, Id);
        GO
        CREATE VIEW dbo.UnpartitionedTargetView WITH SCHEMABINDING AS
        SELECT Grp, Id, Val FROM dbo.UnpartitionedViewTarget;
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_UnpartitionedTargetView ON dbo.UnpartitionedTargetView(Grp, Id) ON PsUnpartitionedView(Grp);
        GO

        CREATE TABLE dbo.NoViewSource (Id INT NOT NULL, Grp INT NOT NULL);
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_NoViewSource ON dbo.NoViewSource(Grp, Id);
        GO
        CREATE TABLE dbo.NoViewTarget (Id INT NOT NULL, Grp INT NOT NULL);
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_NoViewTarget ON dbo.NoViewTarget(Grp, Id);
        """;

    [Fact]
    public async Task TargetReferencedByMoreIndexedViewsThanSource_BlocksSwitchWith11402()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync("ALTER TABLE dbo.CountMismatchSource SWITCH PARTITION 2 TO dbo.CountMismatchTarget PARTITION 2;"));

        Assert.Equal(11402, exception.Number);
    }

    [Fact]
    public async Task LiveCatalogAndScanner_ReportTheIndexedViewReferenceCountMismatch()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        var result = SqlScriptParser.ParseText(
            "test.sql", "ALTER TABLE dbo.CountMismatchSource SWITCH PARTITION 2 TO dbo.CountMismatchTarget PARTITION 2;");

        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var finding = Assert.Single(
            QueryAntiPatternScanner.Scan(result, catalog), f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchIndexedViewAlignment);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("11402", finding.DetailText);
    }

    [Fact]
    public async Task EveryTargetIndexedViewHasAMatchingSourceOne_SwitchSucceeds()
    {
        var exception = await Record.ExceptionAsync(
            () => ExecuteAsync("ALTER TABLE dbo.MatchedSource SWITCH PARTITION 2 TO dbo.MatchedTarget PARTITION 2;"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task LiveCatalogAndScanner_DoesNotReportWhenReferenceCountsAreEqual()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        var result = SqlScriptParser.ParseText(
            "test.sql", "ALTER TABLE dbo.MatchedSource SWITCH PARTITION 2 TO dbo.MatchedTarget PARTITION 2;");

        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.DoesNotContain(
            QueryAntiPatternScanner.Scan(result, catalog), f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchIndexedViewAlignment);
    }

    [Fact]
    public async Task PartitionedTableReferencedByNonPartitionedIndexedView_BlocksSwitchWith11401()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync("ALTER TABLE dbo.UnpartitionedViewSource SWITCH PARTITION 2 TO dbo.UnpartitionedViewTarget PARTITION 2;"));

        Assert.Equal(11401, exception.Number);
    }

    [Fact]
    public async Task LiveCatalogAndScanner_ReportTheNonPartitionedIndexedView()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        var result = SqlScriptParser.ParseText(
            "test.sql", "ALTER TABLE dbo.UnpartitionedViewSource SWITCH PARTITION 2 TO dbo.UnpartitionedViewTarget PARTITION 2;");

        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var finding = Assert.Single(
            QueryAntiPatternScanner.Scan(result, catalog), f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchIndexedViewAlignment);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("11401", finding.DetailText);
    }

    [Fact]
    public async Task NeitherSideHasAnIndexedView_SwitchSucceeds()
    {
        var exception = await Record.ExceptionAsync(() => ExecuteAsync("ALTER TABLE dbo.NoViewSource SWITCH TO dbo.NoViewTarget;"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task LiveCatalogAndScanner_DoesNotReportWhenNeitherSideHasAnIndexedView()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        var result = SqlScriptParser.ParseText("test.sql", "ALTER TABLE dbo.NoViewSource SWITCH TO dbo.NoViewTarget;");

        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.DoesNotContain(
            QueryAntiPatternScanner.Scan(result, catalog), f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchIndexedViewAlignment);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
    }
}
