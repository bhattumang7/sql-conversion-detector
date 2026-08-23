using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class DeadCodeScannerTests
{
    private static IReadOnlyList<DeadCodeFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return DeadCodeScanner.Scan(result);
    }

    [Fact]
    public void StatementAfterUnconditionalReturn_FiresUnreachable()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                RETURN;
                SELECT 1;
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DeadCodeFindingKind.UnreachableCode);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void StatementAfterUnconditionalThrow_FiresUnreachable()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                THROW 50000, 'boom', 1;
                SELECT 1;
            END
            """);

        Assert.Contains(findings, f => f.Kind == DeadCodeFindingKind.UnreachableCode);
    }

    [Fact]
    public void MultipleStatementsAfterReturn_FiresOnlyOnce()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                RETURN;
                SELECT 1;
                SELECT 2;
                SELECT 3;
            END
            """);

        Assert.Single(findings, f => f.Kind == DeadCodeFindingKind.UnreachableCode);
    }

    [Fact]
    public void ReturnAsLastStatement_NeverFiresUnreachable()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                SELECT 1;
                RETURN;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DeadCodeFindingKind.UnreachableCode);
    }

    [Fact]
    public void IfWithOnlyOneBranchTerminal_NeverFiresUnreachable()
    {

        var findings = Scan("""
            CREATE PROCEDURE dbo.P @x INT AS BEGIN
                IF @x = 1 RETURN;
                ELSE SELECT 1;
                SELECT 2;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DeadCodeFindingKind.UnreachableCode);
    }

    [Fact]
    public void IfWithNoElse_NeverFiresUnreachableAfter()
    {

        var findings = Scan("""
            CREATE PROCEDURE dbo.P @x INT AS BEGIN
                IF @x = 1 RETURN;
                SELECT 2;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DeadCodeFindingKind.UnreachableCode);
    }

    [Fact]
    public void IfWithBothBranchesTerminal_FiresUnreachableAfter()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P @x INT AS BEGIN
                IF @x = 1 RETURN;
                ELSE RETURN;
                SELECT 2;
            END
            """);

        Assert.Contains(findings, f => f.Kind == DeadCodeFindingKind.UnreachableCode);
    }

    [Fact]
    public void WhileLoopIsNeverTerminal_CodeAfterNeverFiresUnreachable()
    {

        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                WHILE 1 = 1
                BEGIN
                    RETURN;
                END
                SELECT 1;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DeadCodeFindingKind.UnreachableCode);
    }

    [Fact]
    public void TryCatchBothTerminal_FiresUnreachableAfter()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                BEGIN TRY
                    RETURN;
                END TRY
                BEGIN CATCH
                    THROW;
                END CATCH
                SELECT 1;
            END
            """);

        Assert.Contains(findings, f => f.Kind == DeadCodeFindingKind.UnreachableCode);
    }

    [Fact]
    public void TryCatchOnlyTryTerminal_NeverFiresUnreachableAfter()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                BEGIN TRY
                    RETURN;
                END TRY
                BEGIN CATCH
                    SELECT 1;
                END CATCH
                SELECT 2;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DeadCodeFindingKind.UnreachableCode);
    }

    [Fact]
    public void RoutineContainingGoto_DeclinesUnreachableCodeAnalysisEntirely()
    {

        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                RETURN;
                SELECT 1;
                GOTO Done;
                Done:
                SELECT 2;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DeadCodeFindingKind.UnreachableCode);
    }

    [Fact]
    public void LabelWithNoGoto_FiresUnusedLabel()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                SELECT 1;
                Done:
                SELECT 2;
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DeadCodeFindingKind.UnusedLabel);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void LabelReachedByGoto_NeverFiresUnusedLabel()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                GOTO Done;
                SELECT 1;
                Done:
                SELECT 2;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DeadCodeFindingKind.UnusedLabel);
    }

    [Fact]
    public void DeclaredVariableNeverReferenced_FiresUnusedLocalVariable()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT;
                SELECT 1;
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DeadCodeFindingKind.UnusedLocalVariable && f.DetailText == "@x");
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void DeclaredVariableUsedInPredicate_NeverFiresUnused()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT;
                SET @x = 1;
                SELECT 1 WHERE @x = 1;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DeadCodeFindingKind.UnusedLocalVariable);
    }

    [Fact]
    public void VariableOnlyEverAssignedViaSet_FiresUnusedLocalVariable()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT;
                SET @x = 1;
                SET @x = 2;
            END
            """);

        Assert.Contains(findings, f => f.Kind == DeadCodeFindingKind.UnusedLocalVariable && f.DetailText == "@x");
    }

    [Fact]
    public void VariableOnlyEverAssignedViaSelect_FiresUnusedLocalVariable()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT;
                SELECT @x = Col FROM dbo.T;
            END
            """);

        Assert.Contains(findings, f => f.Kind == DeadCodeFindingKind.UnusedLocalVariable && f.DetailText == "@x");
    }

    [Fact]
    public void VariableReadAfterSelectAssignment_NeverFiresUnused()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT;
                SELECT @x = Col FROM dbo.T;
                SELECT @x;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DeadCodeFindingKind.UnusedLocalVariable);
    }

    [Fact]
    public void CompoundAssignmentReadsPriorValue_CountsAsUse()
    {

        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 0;
                SET @x += 1;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DeadCodeFindingKind.UnusedLocalVariable);
    }

    [Fact]
    public void VariableUsedOnlyAsCursorFetchTarget_NeverFiresUnused()
    {

        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT;
                DECLARE cur CURSOR FOR SELECT Col FROM dbo.T;
                OPEN cur;
                FETCH NEXT FROM cur INTO @x;
                CLOSE cur;
                DEALLOCATE cur;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DeadCodeFindingKind.UnusedLocalVariable);
    }

    [Fact]
    public void ParameterNeverReferenced_FiresUnusedParameter()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P @x INT AS BEGIN
                SELECT 1;
            END
            """);

        Assert.Contains(findings, f => f.Kind == DeadCodeFindingKind.UnusedParameter && f.DetailText == "@x");
    }

    [Fact]
    public void ParameterReferencedInPredicate_NeverFiresUnused()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P @x INT AS BEGIN
                SELECT 1 WHERE @x = 1;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DeadCodeFindingKind.UnusedParameter);
    }

    [Fact]
    public void UnreferencedOutputParameter_NeverFiresUnusedParameter()
    {

        var findings = Scan("""
            CREATE PROCEDURE dbo.P @x INT OUTPUT AS BEGIN
                SELECT 1;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DeadCodeFindingKind.UnusedParameter);
    }

    [Fact]
    public void GotoImmediatelyFollowedByItsOwnLabel_FiresRedundantJump()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                SELECT 1;
                GOTO Done;
                Done:
                SELECT 2;
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DeadCodeFindingKind.RedundantJump);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void GotoJumpingOverRealCode_NeverFiresRedundantJump()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                GOTO Done;
                SELECT 1;
                Done:
                SELECT 2;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DeadCodeFindingKind.RedundantJump);
    }

    [Fact]
    public void RedundantJumpAtRoutineTopLevel_StillFires()
    {

        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            SELECT 1;
            GOTO Done;
            Done:
            SELECT 2;
            """);

        Assert.Contains(findings, f => f.Kind == DeadCodeFindingKind.RedundantJump);
    }

    [Fact]
    public void FunctionBody_NeverAnalyzed()
    {

        var findings = Scan("""
            CREATE FUNCTION dbo.F(@x INT) RETURNS INT AS
            BEGIN
                DECLARE @unused INT;
                RETURN 1;
                SELECT 1;
            END
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void CreateOrAlterProcedure_IsAnalyzed()
    {
        var findings = Scan("""
            CREATE OR ALTER PROCEDURE dbo.P AS BEGIN
                DECLARE @unused INT;
                SELECT 1;
            END
            """);

        Assert.Contains(findings, f => f.Kind == DeadCodeFindingKind.UnusedLocalVariable);
    }

    [Fact]
    public void TriggerBody_IsAnalyzed()
    {
        var findings = Scan("""
            CREATE TRIGGER dbo.TR ON dbo.T AFTER INSERT AS BEGIN
                DECLARE @unused INT;
                SELECT 1;
            END
            """);

        Assert.Contains(findings, f => f.Kind == DeadCodeFindingKind.UnusedLocalVariable);
    }
}
