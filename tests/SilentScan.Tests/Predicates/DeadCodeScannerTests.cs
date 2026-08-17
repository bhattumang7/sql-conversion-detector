using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 4 "Dead and duplicated code" - the five members needing real
/// control-flow/dataflow analysis. Fully syntax-only, no oracle needed - see
/// <see cref="DeadCodeFinding"/>'s own doc comment for the full scope/precision-guard rationale
/// these tests exercise.
/// </summary>
public sealed class DeadCodeScannerTests
{
    private static IReadOnlyList<DeadCodeFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return DeadCodeScanner.Scan(result);
    }

    // --- Unreachable code -------------------------------------------------

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
        // The ELSE branch falls through, so code after the IF is genuinely reachable.
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
        // The implicit else always falls through.
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
        // A WHILE may run zero times, so code after it is always reachable regardless of the
        // body's own terminality - conservative, matching TransactionHygieneScanner's identical
        // WHILE reasoning.
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
        // An arbitrary jump target can make structurally-unreachable code actually reachable -
        // the whole routine's UnreachableCode analysis declines rather than guesses (see
        // DeadCodeFinding's own doc comment).
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

    // --- Unused label -------------------------------------------------------

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

    // --- Unused local variable -----------------------------------------------

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
        // SET @x += ... reads @x's own prior value, unlike a plain SET @x = ..., so this alone
        // counts as a real use even with no other reference anywhere.
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
        // FETCH ... INTO @x is deliberately treated as a real use (a conservative, never-a-
        // false-positive choice - see DeadCodeFinding's own doc comment).
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

    // --- Unused parameter -----------------------------------------------------

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
        // A never-assigned OUTPUT parameter is a distinct, already-shipped, sharper claim
        // (OutputParameterFinding's "unassigned on some path") - deliberately excluded here to
        // avoid two findings restating the same underlying fact differently.
        var findings = Scan("""
            CREATE PROCEDURE dbo.P @x INT OUTPUT AS BEGIN
                SELECT 1;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DeadCodeFindingKind.UnusedParameter);
    }

    // --- Redundant jump ---------------------------------------------------------

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
        // Regression guard: the routine's own outermost statement list is never itself
        // Accept()-ed (Unwrap looks past a single wrapping BEGIN...END), so a redundant jump
        // sitting directly at the top level - not nested inside any IF/WHILE/TRY - must still be
        // caught by the explicit top-level check in RoutineVisitor.Analyze, not only by
        // UsageCollector's own ExplicitVisit(StatementList) override (which only ever sees
        // NESTED lists).
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            SELECT 1;
            GOTO Done;
            Done:
            SELECT 2;
            """);

        Assert.Contains(findings, f => f.Kind == DeadCodeFindingKind.RedundantJump);
    }

    // --- Scope limits -----------------------------------------------------------

    [Fact]
    public void FunctionBody_NeverAnalyzed()
    {
        // Known v1 scope limit: only procedure/trigger bodies are analyzed (matching
        // TransactionHygieneScanner's own established scope) - a function with the exact same
        // unused-variable/unreachable-code shapes is declined, not silently swept in.
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
