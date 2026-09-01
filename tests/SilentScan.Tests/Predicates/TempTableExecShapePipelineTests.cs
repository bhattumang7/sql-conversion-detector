using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

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

        var finding = Assert.Single(report.Find<TempTableExecShapeFinding>("TempTableExecShapeScanner"));
        Assert.Equal(TempTableExecShapeFindingKind.ColumnCountMismatch, finding.Kind);
        Assert.Equal("#Results", finding.TempTableQualifiedName);
        Assert.Equal("dbo.usp_Callee", finding.ExecutedProcQualifiedName);
        Assert.Equal(2, finding.TempTableDeclaredColumnCount);
        Assert.Equal(1, finding.DescribedColumnCount);
    }

    [Fact]
    public async Task ColumnCountMismatch_ExplicitColumnListNarrowerThanDeclared_DefaultTakesOmittedColumn_NeverFires()
    {
        const string sql = """
            CREATE PROCEDURE dbo.usp_Callee AS
            BEGIN
                SELECT CAST(1 AS INT) AS Col1, CAST(2 AS INT) AS Col2;
            END
            GO
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                CREATE TABLE #Results (Col1 INT NOT NULL, Col2 INT NOT NULL, Col3 INT NOT NULL DEFAULT 99);
                INSERT INTO #Results (Col1, Col2) EXEC dbo.usp_Callee;
            END
            """;

        var report = await EngineAuthoritativeScan.ScanAsync(sql);

        Assert.Empty(report.Find<TempTableExecShapeFinding>("TempTableExecShapeScanner"));
    }

    [Fact]
    public async Task ColumnCountMismatch_ExplicitColumnListStillNarrowerThanDescribed_Fires()
    {
        const string sql = """
            CREATE PROCEDURE dbo.usp_Callee AS
            BEGIN
                SELECT CAST(1 AS INT) AS Col1, CAST(2 AS INT) AS Col2, CAST(3 AS INT) AS Col3;
            END
            GO
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                CREATE TABLE #Results (Col1 INT NOT NULL, Col2 INT NOT NULL, Col3 INT NOT NULL DEFAULT 99);
                INSERT INTO #Results (Col1, Col2) EXEC dbo.usp_Callee;
            END
            """;

        var report = await EngineAuthoritativeScan.ScanAsync(sql);

        var finding = Assert.Single(report.Find<TempTableExecShapeFinding>("TempTableExecShapeScanner"));
        Assert.Equal(TempTableExecShapeFindingKind.ColumnCountMismatch, finding.Kind);
        Assert.Equal(2, finding.TempTableDeclaredColumnCount);
        Assert.Equal(3, finding.DescribedColumnCount);
    }

    [Fact]
    public async Task ColumnTypeMismatch_ExplicitColumnListReordered_MatchesByNameNotPosition()
    {
        const string sql = """
            CREATE PROCEDURE dbo.usp_Callee AS
            BEGIN
                SELECT CAST('x' AS VARCHAR(50)) AS Col2, CAST(1 AS INT) AS Col1;
            END
            GO
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                CREATE TABLE #Results (Col1 INT NOT NULL, Col2 VARCHAR(50) NOT NULL);
                INSERT INTO #Results (Col2, Col1) EXEC dbo.usp_Callee;
            END
            """;

        var report = await EngineAuthoritativeScan.ScanAsync(sql);

        Assert.Empty(report.Find<TempTableExecShapeFinding>("TempTableExecShapeScanner"));
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

        var finding = Assert.Single(report.Find<TempTableExecShapeFinding>("TempTableExecShapeScanner"));
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

        Assert.Empty(report.Find<TempTableExecShapeFinding>("TempTableExecShapeScanner"));
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

        Assert.Empty(result.Report.Find<TempTableExecShapeFinding>("TempTableExecShapeScanner"));
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

        Assert.Empty(result.Report.Find<TempTableExecShapeFinding>("TempTableExecShapeScanner"));
        var unanalyzed = Assert.Single(result.TempTableExecShape.Unanalyzed);
        Assert.Contains("OUTPUT", unanalyzed.Reason, StringComparison.Ordinal);
    }
}
