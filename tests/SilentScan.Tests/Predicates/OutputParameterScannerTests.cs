using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second OSS/commercial sweep": "Output parameter not populated on
/// every code path". A real reachability walk, not a heuristic - see
/// <see cref="OutputParameterOracleTests"/> for the real-execution confirmation of the underlying
/// caller-variable-left-unchanged mechanism.
/// </summary>
public sealed class OutputParameterScannerTests
{
    private static IReadOnlyList<OutputParameterFinding> Scan(
        string procedureBody, string parameters = "@x INT OUTPUT")
    {
        var sql = $"CREATE PROCEDURE dbo.p {parameters} AS\nBEGIN\n{procedureBody}\nEND";
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return OutputParameterScanner.Scan(result);
    }

    [Fact]
    public void NeverAssigned_FallsOffEnd_Fires()
    {
        var findings = Scan("SELECT 1;");

        var finding = Assert.Single(findings);
        Assert.Equal(OutputParameterFindingKind.UnassignedOnSomePath, finding.Kind);
        Assert.Equal("@x", finding.ParameterName);
    }

    [Fact]
    public void AssignedUnconditionallyAtTop_NeverFires()
    {
        var findings = Scan("SET @x = 1;\nSELECT 1;");

        Assert.Empty(findings);
    }

    [Fact]
    public void AssignedViaSelectSetVariable_NeverFires()
    {
        var findings = Scan("SELECT @x = 1;");

        Assert.Empty(findings);
    }

    [Fact]
    public void AssignedOnlyInIfBranch_NoElse_ImplicitElseLeavesUnassigned_Fires()
    {
        var findings = Scan(
            """
            IF (1 = 1)
                SET @x = 1;
            """);

        Assert.Single(findings);
    }

    [Fact]
    public void AssignedInBothIfAndElseBranches_NeverFires()
    {
        var findings = Scan(
            """
            IF (1 = 1)
                SET @x = 1;
            ELSE
                SET @x = 2;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void ReturnBeforeAssignment_FiresAtTheReturn()
    {
        var findings = Scan("RETURN;\nSET @x = 1;");

        var finding = Assert.Single(findings);
        Assert.Equal(3, finding.UnresolvedExitLine);
    }

    [Fact]
    public void ReturnAfterAssignment_NeverFires()
    {
        var findings = Scan("SET @x = 1;\nRETURN;");

        Assert.Empty(findings);
    }

    [Fact]
    public void ForwardedAsOutputArgumentToAnotherCall_TreatedAsAssigned_NeverFires()
    {
        var findings = Scan("EXEC dbo.OtherProc @a = @x OUTPUT;");

        Assert.Empty(findings);
    }

    [Fact]
    public void PassedAsPlainInputArgument_NotForwardedAsOutput_StillFires()
    {
        var findings = Scan("EXEC dbo.OtherProc @a = @x;");

        Assert.Single(findings);
    }

    [Fact]
    public void UnconditionalThrow_NeverFires_EvenThoughNeverAssigned()
    {
        // THROW is a real, loud engine error - not the silent defect this rule targets - so it is
        // deliberately never a finding site (see OutputParameterScanner's own doc comment).
        var findings = Scan("THROW 50000, 'boom', 1;");

        Assert.Empty(findings);
    }

    [Fact]
    public void RaiserrorDoesNotTerminateTheWalk_SubsequentAssignmentStillCounts()
    {
        var findings = Scan("RAISERROR('warn', 10, 1);\nSET @x = 1;");

        Assert.Empty(findings);
    }

    [Fact]
    public void GotoAnywhereInBody_DeclinesWholeScope()
    {
        var findings = Scan(
            """
            GOTO Done;
            Done:
            RETURN;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void TryCatch_CatchEntersWithTryStartState_AssignmentOnlyInsideTry_Fires()
    {
        var findings = Scan(
            """
            BEGIN TRY
                SELECT 1;
                SET @x = 1;
            END TRY
            BEGIN CATCH
                SELECT ERROR_MESSAGE();
            END CATCH
            """);

        // CATCH is analyzed entering with the state as of the TRY/CATCH construct's own start -
        // unassigned, since the SET follows a statement that could itself fail - and CATCH never
        // assigns it either.
        Assert.Single(findings);
    }

    [Fact]
    public void TryCatch_BothBranchesAssign_NeverFires()
    {
        var findings = Scan(
            """
            BEGIN TRY
                SET @x = 1;
            END TRY
            BEGIN CATCH
                SET @x = -1;
            END CATCH
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void WhileLoopMayRunZeroTimes_AssignmentOnlyInsideBody_Fires()
    {
        var findings = Scan(
            """
            WHILE (1 = 0)
            BEGIN
                SET @x = 1;
                BREAK;
            END
            """);

        Assert.Single(findings);
    }

    [Fact]
    public void MultipleOutputParameters_OnlyTheUnassignedOneFires()
    {
        var findings = Scan(
            "SET @x = 1;",
            parameters: "@x INT OUTPUT, @y INT OUTPUT");

        var finding = Assert.Single(findings);
        Assert.Equal("@y", finding.ParameterName);
    }

    [Fact]
    public void NoOutputParametersAtAll_NeverFires()
    {
        var findings = Scan("SELECT 1;", parameters: "@x INT");

        Assert.Empty(findings);
    }

    [Fact]
    public void CompoundAssignment_StillCountsAsAssigned()
    {
        var findings = Scan("SET @x = 0;\nSET @x += 1;");

        Assert.Empty(findings);
    }
}
