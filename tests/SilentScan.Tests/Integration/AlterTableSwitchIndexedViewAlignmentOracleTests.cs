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
        GO

        CREATE PARTITION FUNCTION PfDerivedCol (int) AS RANGE LEFT FOR VALUES (10, 20, 30);
        GO
        CREATE PARTITION SCHEME PsDerivedCol AS PARTITION PfDerivedCol ALL TO ([PRIMARY]);
        GO
        CREATE TABLE dbo.DerivedColSource (Id INT NOT NULL, Grp INT NOT NULL, Val INT NOT NULL) ON PsDerivedCol(Grp);
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_DerivedColSource ON dbo.DerivedColSource(Grp, Id) ON PsDerivedCol(Grp);
        GO
        CREATE TABLE dbo.DerivedColTarget (Id INT NOT NULL, Grp INT NOT NULL, Val INT NOT NULL) ON PsDerivedCol(Grp);
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_DerivedColTarget ON dbo.DerivedColTarget(Grp, Id) ON PsDerivedCol(Grp);
        GO
        CREATE VIEW dbo.DerivedColSourceView WITH SCHEMABINDING AS
        SELECT Grp + 0 AS GrpKey, Id, Val FROM dbo.DerivedColSource;
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_DerivedColSourceView ON dbo.DerivedColSourceView(GrpKey, Id) ON PsDerivedCol(GrpKey);
        GO

        CREATE TABLE dbo.WrongColSource (Id INT NOT NULL, Grp INT NOT NULL, Val INT NOT NULL) ON PsDerivedCol(Grp);
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_WrongColSource ON dbo.WrongColSource(Grp, Id) ON PsDerivedCol(Grp);
        GO
        CREATE TABLE dbo.WrongColTarget (Id INT NOT NULL, Grp INT NOT NULL, Val INT NOT NULL) ON PsDerivedCol(Grp);
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_WrongColTarget ON dbo.WrongColTarget(Grp, Id) ON PsDerivedCol(Grp);
        GO
        CREATE VIEW dbo.WrongColSourceView WITH SCHEMABINDING AS
        SELECT Val AS GrpKey, Id, Grp FROM dbo.WrongColSource;
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_WrongColSourceView ON dbo.WrongColSourceView(GrpKey, Id) ON PsDerivedCol(GrpKey);
        GO

        CREATE PARTITION FUNCTION PfFunctionMismatch (int) AS RANGE LEFT FOR VALUES (10, 20, 30);
        GO
        CREATE PARTITION SCHEME PsFunctionMismatch AS PARTITION PfFunctionMismatch ALL TO ([PRIMARY]);
        GO
        CREATE PARTITION FUNCTION PfFunctionMismatchView (int) AS RANGE LEFT FOR VALUES (10, 20, 30, 999);
        GO
        CREATE PARTITION SCHEME PsFunctionMismatchView AS PARTITION PfFunctionMismatchView ALL TO ([PRIMARY]);
        GO
        CREATE TABLE dbo.FunctionMismatchSource (Id INT NOT NULL, Grp INT NOT NULL, Val INT NOT NULL) ON PsFunctionMismatch(Grp);
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_FunctionMismatchSource ON dbo.FunctionMismatchSource(Grp, Id) ON PsFunctionMismatch(Grp);
        GO
        CREATE TABLE dbo.FunctionMismatchTarget (Id INT NOT NULL, Grp INT NOT NULL, Val INT NOT NULL) ON PsFunctionMismatch(Grp);
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_FunctionMismatchTarget ON dbo.FunctionMismatchTarget(Grp, Id) ON PsFunctionMismatch(Grp);
        GO
        CREATE VIEW dbo.FunctionMismatchSourceView WITH SCHEMABINDING AS
        SELECT Grp, Id, Val FROM dbo.FunctionMismatchSource;
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_FunctionMismatchSourceView ON dbo.FunctionMismatchSourceView(Grp, Id) ON PsFunctionMismatchView(Grp);
        GO

        CREATE PARTITION SCHEME PsFunctionEquivalentAlias AS PARTITION PfFunctionMismatch ALL TO ([PRIMARY]);
        GO
        CREATE TABLE dbo.FunctionEquivalentSource (Id INT NOT NULL, Grp INT NOT NULL, Val INT NOT NULL) ON PsFunctionMismatch(Grp);
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_FunctionEquivalentSource ON dbo.FunctionEquivalentSource(Grp, Id) ON PsFunctionMismatch(Grp);
        GO
        CREATE TABLE dbo.FunctionEquivalentTarget (Id INT NOT NULL, Grp INT NOT NULL, Val INT NOT NULL) ON PsFunctionMismatch(Grp);
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_FunctionEquivalentTarget ON dbo.FunctionEquivalentTarget(Grp, Id) ON PsFunctionMismatch(Grp);
        GO
        CREATE VIEW dbo.FunctionEquivalentSourceView WITH SCHEMABINDING AS
        SELECT Grp, Id, Val FROM dbo.FunctionEquivalentSource;
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_FunctionEquivalentSourceView ON dbo.FunctionEquivalentSourceView(Grp, Id) ON PsFunctionEquivalentAlias(Grp);
        GO
        CREATE VIEW dbo.FunctionEquivalentTargetView WITH SCHEMABINDING AS
        SELECT Grp, Id, Val FROM dbo.FunctionEquivalentTarget;
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_FunctionEquivalentTargetView ON dbo.FunctionEquivalentTargetView(Grp, Id) ON PsFunctionMismatch(Grp);
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

    [Fact]
    public async Task IndexedViewPartitioningColumnIsDerivedExpression_BlocksSwitchWith11403()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync("ALTER TABLE dbo.DerivedColSource SWITCH PARTITION 2 TO dbo.DerivedColTarget PARTITION 2;"));

        Assert.Equal(11403, exception.Number);
    }

    [Fact]
    public async Task LiveCatalogAndScanner_ReportTheDerivedExpressionPartitioningColumn()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        var result = SqlScriptParser.ParseText(
            "test.sql", "ALTER TABLE dbo.DerivedColSource SWITCH PARTITION 2 TO dbo.DerivedColTarget PARTITION 2;");

        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var finding = Assert.Single(
            QueryAntiPatternScanner.Scan(result, catalog), f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchIndexedViewAlignment);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("11403", finding.DetailText);
    }

    [Fact]
    public async Task IndexedViewPartitioningColumnIsDirectlySelectedFromADifferentColumn_BlocksSwitchWith11405()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync("ALTER TABLE dbo.WrongColSource SWITCH PARTITION 2 TO dbo.WrongColTarget PARTITION 2;"));

        Assert.Equal(11405, exception.Number);
    }

    [Fact]
    public async Task LiveCatalogAndScanner_ReportTheDirectlySelectedDifferentColumn()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        var result = SqlScriptParser.ParseText(
            "test.sql", "ALTER TABLE dbo.WrongColSource SWITCH PARTITION 2 TO dbo.WrongColTarget PARTITION 2;");

        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var finding = Assert.Single(
            QueryAntiPatternScanner.Scan(result, catalog), f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchIndexedViewAlignment);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("11405", finding.DetailText);
    }

    [Fact]
    public async Task IndexedViewOnNonEquivalentPartitionFunction_BlocksSwitchWith11400()
    {
        var exception = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync("ALTER TABLE dbo.FunctionMismatchSource SWITCH PARTITION 2 TO dbo.FunctionMismatchTarget PARTITION 2;"));

        Assert.Equal(11400, exception.Number);
    }

    [Fact]
    public async Task LiveCatalogAndScanner_ReportTheNonEquivalentPartitionFunction()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        var result = SqlScriptParser.ParseText(
            "test.sql", "ALTER TABLE dbo.FunctionMismatchSource SWITCH PARTITION 2 TO dbo.FunctionMismatchTarget PARTITION 2;");

        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var finding = Assert.Single(
            QueryAntiPatternScanner.Scan(result, catalog), f => f.Kind == QueryAntiPatternFindingKind.AlterTableSwitchIndexedViewAlignment);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("11400", finding.DetailText);
    }

    [Fact]
    public async Task IndexedViewsOnDifferentlyNamedButEquivalentPartitionFunctions_SwitchSucceeds()
    {
        var exception = await Record.ExceptionAsync(
            () => ExecuteAsync("ALTER TABLE dbo.FunctionEquivalentSource SWITCH PARTITION 2 TO dbo.FunctionEquivalentTarget PARTITION 2;"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task LiveCatalogAndScanner_DoesNotReportDifferentlyNamedButEquivalentPartitionFunctions()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();
        var result = SqlScriptParser.ParseText(
            "test.sql", "ALTER TABLE dbo.FunctionEquivalentSource SWITCH PARTITION 2 TO dbo.FunctionEquivalentTarget PARTITION 2;");

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
