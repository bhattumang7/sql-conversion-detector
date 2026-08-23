using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class ForcedSerialScannerTests
{
    private static IReadOnlyList<ForcedSerialFinding> Scan(string sql)
    {
        var ddl = "CREATE TABLE dbo.T (Id INT NOT NULL, Val INT NULL);";
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        return ForcedSerialScanner.Scan(result);
    }

    [Fact]
    public void TableVariable_InsertTarget_Fires()
    {
        var findings = Scan("DECLARE @t TABLE (Id INT); INSERT INTO @t (Id) SELECT Id FROM dbo.T;");

        var finding = Assert.Single(findings);
        Assert.Equal(ForcedSerialFindingKind.TableVariableModification, finding.Kind);
        Assert.Equal("@t", finding.DetailText);
    }

    [Fact]
    public void TableVariable_UpdateTarget_Fires()
    {
        var findings = Scan("DECLARE @t TABLE (Id INT, Val INT); UPDATE @t SET Val = 1;");

        var finding = Assert.Single(findings);
        Assert.Equal(ForcedSerialFindingKind.TableVariableModification, finding.Kind);
    }

    [Fact]
    public void TableVariable_DeleteTarget_Fires()
    {
        var findings = Scan("DECLARE @t TABLE (Id INT); DELETE FROM @t;");

        Assert.Single(findings);
    }

    [Fact]
    public void TableVariable_OutputIntoTarget_Fires()
    {
        var findings = Scan("DECLARE @out TABLE (Id INT); DELETE FROM dbo.T OUTPUT deleted.Id INTO @out;");

        var finding = Assert.Single(findings);
        Assert.Equal("@out", finding.DetailText);
    }

    [Fact]
    public void TableVariable_OutputIntoRealTable_NeverFires()
    {
        var findings = Scan("CREATE TABLE dbo.Audit (Id INT); DELETE FROM dbo.T OUTPUT deleted.Id INTO dbo.Audit;");

        Assert.Empty(findings);
    }

    [Fact]
    public void TableVariable_ReadOnlyReference_NeverFires()
    {
        var findings = Scan("DECLARE @t TABLE (Id INT); INSERT INTO @t (Id) VALUES (1); SELECT Id FROM @t JOIN dbo.T ON @t.Id = dbo.T.Id;");

        Assert.Single(findings);
    }

    [Fact]
    public void TableVariable_ScopedPerBatch_UnrelatedBatchNeverFires()
    {
        var findings = Scan(
            """
            DECLARE @t TABLE (Id INT);
            INSERT INTO @t (Id) VALUES (1);
            GO
            DECLARE @t TABLE (Id INT);
            SELECT Id FROM @t;
            """);

        Assert.Single(findings);
    }

    [Fact]
    public void Cursor_FastForward_Fires()
    {
        var findings = Scan("DECLARE c CURSOR FAST_FORWARD FOR SELECT Id FROM dbo.T; OPEN c; CLOSE c; DEALLOCATE c;");

        var finding = Assert.Single(findings);
        Assert.Equal(ForcedSerialFindingKind.FastForwardCursor, finding.Kind);
        Assert.Equal("c", finding.DetailText);
    }

    [Fact]
    public void Cursor_BareForwardOnlyReadOnly_Fires()
    {
        var findings = Scan("DECLARE c CURSOR FORWARD_ONLY READ_ONLY FOR SELECT Id FROM dbo.T; OPEN c; CLOSE c; DEALLOCATE c;");

        Assert.Single(findings);
    }

    [Fact]
    public void Cursor_LocalStaticForwardOnlyReadOnly_NeverFires()
    {
        var findings = Scan("DECLARE c CURSOR LOCAL STATIC FORWARD_ONLY READ_ONLY FOR SELECT Id FROM dbo.T; OPEN c; CLOSE c; DEALLOCATE c;");

        Assert.Empty(findings);
    }

    [Fact]
    public void Cursor_NoOptions_NeverFires()
    {
        var findings = Scan("DECLARE c CURSOR FOR SELECT Id FROM dbo.T; OPEN c; CLOSE c; DEALLOCATE c;");

        Assert.Empty(findings);
    }

    [Fact]
    public void Cursor_Dynamic_NeverFires()
    {
        var findings = Scan("DECLARE c CURSOR DYNAMIC FOR SELECT Id FROM dbo.T; OPEN c; CLOSE c; DEALLOCATE c;");

        Assert.Empty(findings);
    }

    [Fact]
    public void CursorVariable_FastForward_Fires()
    {
        var findings = Scan("DECLARE @c CURSOR; SET @c = CURSOR FAST_FORWARD FOR SELECT Id FROM dbo.T; OPEN @c; CLOSE @c; DEALLOCATE @c;");

        var finding = Assert.Single(findings);
        Assert.Equal(ForcedSerialFindingKind.FastForwardCursor, finding.Kind);
        Assert.Equal("@c", finding.DetailText);
    }

    [Fact]
    public void Intrinsic_ObjectId_InsideQueryWithFrom_Fires()
    {
        var findings = Scan("SELECT Id FROM dbo.T WHERE OBJECT_ID('dbo.T') IS NOT NULL;");

        var finding = Assert.Single(findings);
        Assert.Equal(ForcedSerialFindingKind.NonParallelizableIntrinsic, finding.Kind);
        Assert.Equal("OBJECT_ID", finding.DetailText);
    }

    [Fact]
    public void Intrinsic_ErrorNumber_InsideQueryWithFrom_Fires()
    {
        var findings = Scan("SELECT Id, ERROR_NUMBER() FROM dbo.T;");

        Assert.Single(findings);
    }

    [Fact]
    public void Intrinsic_TranCount_InsideQueryWithFrom_Fires()
    {
        var findings = Scan("SELECT Id FROM dbo.T WHERE @@TRANCOUNT > 0;");

        var finding = Assert.Single(findings);
        Assert.Equal("@@TRANCOUNT", finding.DetailText);
    }

    [Fact]
    public void Intrinsic_NoFromClause_NeverFires()
    {
        var findings = Scan("SELECT OBJECT_ID('dbo.T');");

        Assert.Empty(findings);
    }

    [Fact]
    public void Intrinsic_RowCount_NeverFires()
    {

        var findings = Scan("SELECT Id FROM dbo.T WHERE @@ROWCOUNT > 0;");

        Assert.Empty(findings);
    }

    [Fact]
    public void Intrinsic_ScopeIdentity_NeverFires()
    {
        var findings = Scan("SELECT Id FROM dbo.T WHERE Id = SCOPE_IDENTITY();");

        Assert.Empty(findings);
    }
}
