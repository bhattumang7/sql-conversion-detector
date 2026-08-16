using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "Dynamic SQL quality" item 3 - the site-discovery half,
/// pure AST + catalog, no database round trip. The live-round-trip half
/// (<c>SilentScan.Live.Catalog.TempTableExecShapeChecker</c>) is covered separately.
/// </summary>
public sealed class TempTableExecShapeCandidateScannerTests
{
    private static IReadOnlyList<TempTableExecShapeCandidate> Scan(string sql)
    {
        var parseResult = SqlScriptParser.ParseText("proc.sql", sql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([parseResult]);
        return TempTableExecShapeCandidateScanner.Scan(parseResult, catalog);
    }

    [Fact]
    public void InsertIntoTempTableFromNamedProcedureExec_IsACandidate()
    {
        var candidates = Scan("""
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                CREATE TABLE #Results (Id INT NOT NULL, Name VARCHAR(50) NOT NULL);
                INSERT INTO #Results EXEC dbo.usp_Callee;
            END
            """);

        var candidate = Assert.Single(candidates);
        Assert.Equal("#Results", candidate.TempTableQualifiedName);
        Assert.Equal("dbo.usp_Callee", candidate.ExecutedProcQualifiedName);
        Assert.Equal("dbo.usp_Caller", candidate.CallerScopeQualifiedName);
        Assert.NotNull(candidate.TempTableColumns);
        Assert.Equal(2, candidate.TempTableColumns!.Count);
    }

    [Fact]
    public void InsertIntoTempTableFromStringExec_IsNotACandidate()
    {
        var candidates = Scan("""
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                CREATE TABLE #Results (Id INT NOT NULL);
                INSERT INTO #Results EXEC('SELECT 1');
            END
            """);

        Assert.Empty(candidates);
    }

    [Fact]
    public void InsertIntoTempTableFromDynamicVariableExec_IsNotACandidate()
    {
        var candidates = Scan("""
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1';
                CREATE TABLE #Results (Id INT NOT NULL);
                INSERT INTO #Results EXEC(@sql);
            END
            """);

        Assert.Empty(candidates);
    }

    [Fact]
    public void InsertIntoRealTable_IsNotACandidate()
    {
        var candidates = Scan("""
            CREATE TABLE dbo.Results (Id INT NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                INSERT INTO dbo.Results EXEC dbo.usp_Callee;
            END
            """);

        Assert.Empty(candidates);
    }

    [Fact]
    public void InsertWithOrdinarySelectSource_IsNotACandidate()
    {
        var candidates = Scan("""
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                CREATE TABLE #Results (Id INT NOT NULL);
                INSERT INTO #Results SELECT 1;
            END
            """);

        Assert.Empty(candidates);
    }

    [Fact]
    public void TempTableDeclaredOutsideAnyProcedure_TempTableColumnsIsNullNotGuessed()
    {
        // A batch-level #temp table has no enclosing proc scope (_currentProcScope stays null for
        // top-level statements) - the candidate is still reported (the site is real), but with an
        // honest null TempTableColumns rather than a wrong lookup guessing at some other scope.
        var candidates = Scan("""
            CREATE TABLE #Results (Id INT NOT NULL);
            INSERT INTO #Results EXEC dbo.usp_Callee;
            """);

        var candidate = Assert.Single(candidates);
        Assert.NotNull(candidate.TempTableColumns);
    }

    [Fact]
    public void UnresolvedTempTable_TempTableColumnsIsNull()
    {
        // #Results is never CREATEd anywhere this pass can see - reported honestly as
        // unresolved (null), never guessed at some assumed shape.
        var candidates = Scan("""
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                INSERT INTO #Results EXEC dbo.usp_Callee;
            END
            """);

        var candidate = Assert.Single(candidates);
        Assert.Null(candidate.TempTableColumns);
    }
}
