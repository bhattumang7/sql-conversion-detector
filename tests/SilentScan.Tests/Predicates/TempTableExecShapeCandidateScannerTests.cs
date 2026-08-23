using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

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
