using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// End-to-end proof (docs/detection-checklist.md Tier 2 "Dynamic SQL quality" item 3, temp-table
/// shape mismatch across a proc-call boundary) that a real deployed caller/callee pair surfaces
/// through the SAME live pipeline production's <c>scan-db</c> uses
/// (<see cref="Support.EngineAuthoritativeScan"/>), not just that
/// <c>SilentScan.Live.Catalog.TempTableExecShapeChecker.Classify</c>'s own logic is correct
/// against hand-built inputs (<see cref="Catalog.TempTableExecShapeCheckerClassifyTests"/>). This
/// is also the only place the new <c>LiveReadOnlyGuard.AssertDescribeFirstResultSetProbeOnly</c>
/// carve-out and <c>sys.dm_exec_describe_first_result_set(N'EXEC dbo.proc', ...)</c> get proven
/// against a REAL server round trip - <see cref="Catalog.LiveReadOnlyGuardTests"/> only proves the
/// guard parses the shapes it should accept/reject, not that the DMV itself accepts an EXEC-form
/// probe text end to end.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class TempTableExecShapePipelineTests
{
    [Fact]
    public async Task ColumnCountMismatch_Fires()
    {
        const string sql = """
            CREATE PROCEDURE dbo.usp_Callee AS
            BEGIN
                SELECT 1 AS Id;
            END
            GO
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                CREATE TABLE #Results (Id INT NOT NULL, Name VARCHAR(50) NOT NULL);
                INSERT INTO #Results EXEC dbo.usp_Callee;
            END
            """;

        var report = await EngineAuthoritativeScan.ScanAsync(sql);

        var finding = Assert.Single(report.TempTableExecShapeFindings);
        Assert.Equal(TempTableExecShapeFindingKind.ColumnCountMismatch, finding.Kind);
        Assert.Equal("#Results", finding.TempTableQualifiedName);
        Assert.Equal("dbo.usp_Callee", finding.ExecutedProcQualifiedName);
        Assert.Equal(2, finding.TempTableDeclaredColumnCount);
        Assert.Equal(1, finding.DescribedColumnCount);
    }

    [Fact]
    public async Task ColumnTypeMismatch_UnicodeIntoNonUnicode_Fires()
    {
        const string sql = """
            CREATE PROCEDURE dbo.usp_Callee AS
            BEGIN
                SELECT CAST(N'x' AS NVARCHAR(100)) AS Name;
            END
            GO
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                CREATE TABLE #Results (Name VARCHAR(50) NOT NULL);
                INSERT INTO #Results EXEC dbo.usp_Callee;
            END
            """;

        var report = await EngineAuthoritativeScan.ScanAsync(sql);

        var finding = Assert.Single(report.TempTableExecShapeFindings);
        Assert.Equal(TempTableExecShapeFindingKind.ColumnTypeMismatch, finding.Kind);
        Assert.Equal(1, finding.ColumnPosition);
        Assert.Equal("Name", finding.ColumnName);
        Assert.Equal(WriteLossKind.UnicodeToNonUnicodeReplacement, finding.WriteLoss);
    }

    [Fact]
    public async Task MatchingShape_NeverFires()
    {
        const string sql = """
            CREATE PROCEDURE dbo.usp_Callee AS
            BEGIN
                SELECT CAST(1 AS INT) AS Id, CAST('x' AS VARCHAR(50)) AS Name;
            END
            GO
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                CREATE TABLE #Results (Id INT NOT NULL, Name VARCHAR(50) NOT NULL);
                INSERT INTO #Results EXEC dbo.usp_Callee;
            END
            """;

        var report = await EngineAuthoritativeScan.ScanAsync(sql);

        Assert.Empty(report.TempTableExecShapeFindings);
    }

    [Fact]
    public async Task ExecutedProcDoesNotExist_ReportsUnanalyzedNotAFinding()
    {
        const string sql = """
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                CREATE TABLE #Results (Id INT NOT NULL);
                INSERT INTO #Results EXEC dbo.usp_DoesNotExist;
            END
            """;

        var result = await EngineAuthoritativeScan.RunAsync(sql);

        Assert.Empty(result.Report.TempTableExecShapeFindings);
        var unanalyzed = Assert.Single(result.TempTableExecShape.Unanalyzed);
        Assert.Equal("dbo.usp_DoesNotExist", unanalyzed.ExecutedProcQualifiedName);
    }

    [Fact]
    public async Task OutputParameter_ReportsUnanalyzedNotAFinding()
    {
        const string sql = """
            CREATE PROCEDURE dbo.usp_Callee @Total INT OUTPUT AS
            BEGIN
                SET @Total = 1;
                SELECT 1 AS Id;
            END
            GO
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                CREATE TABLE #Results (Id INT NOT NULL);
                INSERT INTO #Results EXEC dbo.usp_Callee @Total = 0;
            END
            """;

        var result = await EngineAuthoritativeScan.RunAsync(sql);

        Assert.Empty(result.Report.TempTableExecShapeFindings);
        var unanalyzed = Assert.Single(result.TempTableExecShape.Unanalyzed);
        Assert.Contains("OUTPUT", unanalyzed.Reason, StringComparison.Ordinal);
    }
}
