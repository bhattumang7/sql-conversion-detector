using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "Small precise adds", "Transaction hygiene pair" (first half):
/// BEGIN TRANSACTION with no reachable COMMIT/ROLLBACK on some path. A real reachability walk,
/// not a heuristic - see <see cref="TransactionHygieneOracleTests"/> for the real-execution
/// confirmation of the underlying @@TRANCOUNT mechanism.
/// </summary>
public sealed class TransactionHygieneScannerTests
{
    private static IReadOnlyList<TransactionHygieneFinding> Scan(string procedureBody)
    {
        var sql = $"CREATE PROCEDURE dbo.p AS\nBEGIN\n{procedureBody}\nEND";
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return TransactionHygieneScanner.Scan(result);
    }

    [Fact]
    public void NoCommitOrRollback_FallsOffEnd_Fires()
    {
        var findings = Scan("BEGIN TRANSACTION;\nSELECT 1;");

        Assert.Single(findings);
    }

    [Fact]
    public void CommitAtEnd_NeverFires()
    {
        var findings = Scan("BEGIN TRANSACTION;\nSELECT 1;\nCOMMIT TRANSACTION;");

        Assert.Empty(findings);
    }

    [Fact]
    public void RollbackAtEnd_NeverFires()
    {
        var findings = Scan("BEGIN TRANSACTION;\nSELECT 1;\nROLLBACK TRANSACTION;");

        Assert.Empty(findings);
    }

    [Fact]
    public void ReturnWhileOpen_FiresAtTheReturn()
    {
        var findings = Scan("BEGIN TRANSACTION;\nRETURN;");

        var finding = Assert.Single(findings);
        Assert.Equal(TransactionHygieneFindingKind.UnresolvedOnSomePath, finding.Kind);
    }

    [Fact]
    public void ReturnAfterCommit_NeverFires()
    {
        var findings = Scan("BEGIN TRANSACTION;\nCOMMIT TRANSACTION;\nRETURN;");

        Assert.Empty(findings);
    }

    [Fact]
    public void IfBothBranchesResolve_NeverFires()
    {
        var findings = Scan(
            """
            BEGIN TRANSACTION;
            IF (1 = 1)
                COMMIT TRANSACTION;
            ELSE
                ROLLBACK TRANSACTION;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void IfNoElse_ImplicitElseLeavesOpen_ThenFallsOffEnd_Fires()
    {
        var findings = Scan(
            """
            BEGIN TRANSACTION;
            IF (1 = 1)
            BEGIN
                COMMIT TRANSACTION;
                RETURN;
            END
            SELECT 1;
            """);

        // The THEN branch resolves and returns cleanly; the implicit ELSE path falls through
        // still open and reaches the end of the procedure body unresolved.
        Assert.Single(findings);
    }

    [Fact]
    public void IfNoElse_ThenBranchFollowedByUnconditionalRollback_NeverFires()
    {
        var findings = Scan(
            """
            BEGIN TRANSACTION;
            IF (1 = 1)
            BEGIN
                COMMIT TRANSACTION;
                RETURN;
            END
            ROLLBACK TRANSACTION;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void TryCatchBothResolve_NeverFires()
    {
        var findings = Scan(
            """
            BEGIN TRANSACTION;
            BEGIN TRY
                SELECT 1;
                COMMIT TRANSACTION;
            END TRY
            BEGIN CATCH
                ROLLBACK TRANSACTION;
            END CATCH
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void TryCommitsButCatchNeverRollsBack_Fires()
    {
        var findings = Scan(
            """
            BEGIN TRANSACTION;
            BEGIN TRY
                SELECT 1;
                COMMIT TRANSACTION;
            END TRY
            BEGIN CATCH
                SELECT ERROR_MESSAGE();
            END CATCH
            """);

        // CATCH is analyzed entering with the state as of the TRY/CATCH construct's own start -
        // open, since BEGIN TRANSACTION precedes it - and CATCH itself never resolves.
        Assert.Single(findings);
    }

    [Fact]
    public void CatchThrowsWithoutRollback_FiresAtTheThrow()
    {
        var findings = Scan(
            """
            BEGIN TRANSACTION;
            BEGIN TRY
                SELECT 1;
                COMMIT TRANSACTION;
            END TRY
            BEGIN CATCH
                THROW;
            END CATCH
            """);

        Assert.Single(findings);
    }

    [Fact]
    public void WhileLoopMayRunZeroTimes_CommitOnlyInsideBody_Fires()
    {
        var findings = Scan(
            """
            BEGIN TRANSACTION;
            WHILE (1 = 0)
            BEGIN
                COMMIT TRANSACTION;
                BREAK;
            END
            """);

        // The loop's own zero-iteration path leaves the transaction open, falling off the end
        // of the procedure unresolved.
        Assert.Single(findings);
    }

    [Fact]
    public void WhileLoopFollowedByUnconditionalCommit_NeverFires()
    {
        var findings = Scan(
            """
            BEGIN TRANSACTION;
            WHILE (1 = 0)
            BEGIN
                SELECT 1;
            END
            COMMIT TRANSACTION;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void NestedBeginTransactionAlreadyOpen_DeclinesRatherThanGuesses()
    {
        var findings = Scan(
            """
            BEGIN TRANSACTION;
            BEGIN TRANSACTION;
            COMMIT TRANSACTION;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void GotoAnywhereInBody_DeclinesWholeScope()
    {
        var findings = Scan(
            """
            BEGIN TRANSACTION;
            GOTO Done;
            Done:
            RETURN;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void NoTransactionAtAll_NeverFires()
    {
        var findings = Scan("SELECT 1;\nRETURN;");

        Assert.Empty(findings);
    }

    [Fact]
    public void SaveTransactionDoesNotResolveTheOuterOne_Fires()
    {
        var findings = Scan(
            """
            BEGIN TRANSACTION;
            SAVE TRANSACTION sp1;
            RETURN;
            """);

        Assert.Single(findings);
    }

    [Fact]
    public void SequentialTransactions_FirstResolvedSecondNot_OneFindingAnchoredAtSecond()
    {
        var findings = Scan(
            """
            BEGIN TRANSACTION;
            COMMIT TRANSACTION;
            BEGIN TRANSACTION;
            RETURN;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(5, finding.BeginTransactionLine);
    }

    [Fact]
    public void TriggerBody_SameShapeFires()
    {
        var sql =
            """
            CREATE TRIGGER dbo.tr_test ON dbo.SomeTable FOR INSERT
            AS
            BEGIN
                BEGIN TRANSACTION;
                RETURN;
            END
            """;
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var findings = TransactionHygieneScanner.Scan(result);

        Assert.Single(findings);
    }
}
