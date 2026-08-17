using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 4 "Cursor and control-flow correctness". See <see
/// cref="ControlFlowRiskFinding"/> for full scope - "an output parameter never assigned" is already
/// covered by the separately-shipped <see cref="OutputParameterFinding"/>, not tested here.
/// </summary>
public sealed class ControlFlowRiskScannerTests
{
    private static IReadOnlyList<ControlFlowRiskFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return ControlFlowRiskScanner.Scan(result);
    }

    // --- CursorFetchColumnCountMismatch ---

    [Fact]
    public void FetchIntoFewerVariablesThanCursorColumns_Fires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                DECLARE @a INT, @b INT;
                DECLARE cur CURSOR FOR SELECT X, Y, Z FROM dbo.T;
                OPEN cur;
                FETCH NEXT FROM cur INTO @a, @b;
                CLOSE cur;
                DEALLOCATE cur;
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == ControlFlowRiskFindingKind.CursorFetchColumnCountMismatch);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void FetchIntoMoreVariablesThanCursorColumns_Fires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                DECLARE @a INT, @b INT, @c INT;
                DECLARE cur CURSOR FOR SELECT X, Y FROM dbo.T;
                OPEN cur;
                FETCH NEXT FROM cur INTO @a, @b, @c;
                CLOSE cur;
                DEALLOCATE cur;
            END
            """);

        Assert.Contains(findings, f => f.Kind == ControlFlowRiskFindingKind.CursorFetchColumnCountMismatch);
    }

    [Fact]
    public void FetchIntoMatchingColumnCount_NeverFires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                DECLARE @a INT, @b INT;
                DECLARE cur CURSOR FOR SELECT X, Y FROM dbo.T;
                OPEN cur;
                FETCH NEXT FROM cur INTO @a, @b;
                CLOSE cur;
                DEALLOCATE cur;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == ControlFlowRiskFindingKind.CursorFetchColumnCountMismatch);
    }

    [Fact]
    public void CursorSourceIsSelectStar_DeclinesRatherThanGuesses()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                DECLARE @a INT;
                DECLARE cur CURSOR FOR SELECT * FROM dbo.T;
                OPEN cur;
                FETCH NEXT FROM cur INTO @a;
                CLOSE cur;
                DEALLOCATE cur;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == ControlFlowRiskFindingKind.CursorFetchColumnCountMismatch);
    }

    // --- EmptyCatchBlock ---

    [Fact]
    public void EmptyCatchBlock_Fires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                BEGIN TRY
                    SELECT 1;
                END TRY
                BEGIN CATCH
                END CATCH
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == ControlFlowRiskFindingKind.EmptyCatchBlock);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void EmptyCatchBlock_ReportsARealLineNotASentinel()
    {
        // Regression guard: an empty StatementList carries no token span of its own (ScriptDom
        // leaves its StartLine at -1 for a zero-statement list) - a first version of this scanner
        // reported that raw -1 straight through, caught only by running against the real corpus,
        // not by this test suite alone.
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                BEGIN TRY
                    SELECT 1;
                END TRY
                BEGIN CATCH
                END CATCH
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == ControlFlowRiskFindingKind.EmptyCatchBlock);
        Assert.True(finding.Line > 0, $"expected a real, positive line number, got {finding.Line}");
        Assert.True(finding.Column > 0, $"expected a real, positive column number, got {finding.Column}");
    }

    [Fact]
    public void CatchBlockWithStatements_NeverFires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                BEGIN TRY
                    SELECT 1;
                END TRY
                BEGIN CATCH
                    THROW;
                END CATCH
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == ControlFlowRiskFindingKind.EmptyCatchBlock);
    }

    // --- TriggerEmitsOutput ---

    [Fact]
    public void SelectInTrigger_Fires()
    {
        var findings = Scan("CREATE TRIGGER dbo.Trg ON dbo.T AFTER INSERT AS BEGIN SELECT * FROM inserted; END");

        var finding = Assert.Single(findings, f => f.Kind == ControlFlowRiskFindingKind.TriggerEmitsOutput);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void PrintInTrigger_Fires()
    {
        var findings = Scan("CREATE TRIGGER dbo.Trg ON dbo.T AFTER INSERT AS BEGIN PRINT 'fired'; END");

        Assert.Contains(findings, f => f.Kind == ControlFlowRiskFindingKind.TriggerEmitsOutput);
    }

    [Fact]
    public void AssignmentOnlySelectInTrigger_NeverFires()
    {
        var findings = Scan("""
            CREATE TRIGGER dbo.Trg ON dbo.T AFTER INSERT AS
            BEGIN
                DECLARE @id INT;
                SELECT @id = Id FROM inserted;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == ControlFlowRiskFindingKind.TriggerEmitsOutput);
    }

    [Fact]
    public void SelectIntoInTrigger_NeverFires()
    {
        var findings = Scan("CREATE TRIGGER dbo.Trg ON dbo.T AFTER INSERT AS BEGIN SELECT * INTO #tmp FROM inserted; END");

        Assert.DoesNotContain(findings, f => f.Kind == ControlFlowRiskFindingKind.TriggerEmitsOutput);
    }

    [Fact]
    public void CursorDefiningSelectInTrigger_NeverFiresTriggerOutput()
    {
        // Regression guard: a cursor's own DECLARE cur CURSOR FOR SELECT ... never sends a
        // client-visible result set - it only supplies the cursor's row source. A real false
        // positive caught only by running against the real corpus, not by this test suite alone:
        // the first version of this scanner flagged this shape as trigger output.
        var findings = Scan("""
            CREATE TRIGGER dbo.Trg ON dbo.T AFTER INSERT AS
            BEGIN
                DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT Id FROM inserted;
                OPEN cur;
                CLOSE cur;
                DEALLOCATE cur;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == ControlFlowRiskFindingKind.TriggerEmitsOutput);
    }

    [Fact]
    public void NoLockInsideCursorDefiningSelectInTrigger_StillFiresDirtyRead()
    {
        // The cursor-defining-SELECT exclusion above must be narrow: it excludes only the
        // TriggerEmitsOutput check, never the other checks that can legitimately fire inside the
        // same SELECT.
        var findings = Scan("""
            CREATE TRIGGER dbo.Trg ON dbo.T AFTER INSERT AS
            BEGIN
                DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT Id FROM dbo.Other WITH (NOLOCK);
                OPEN cur;
                CLOSE cur;
                DEALLOCATE cur;
            END
            """);

        Assert.Contains(findings, f => f.Kind == ControlFlowRiskFindingKind.DirtyReadIsolationHint);
    }

    [Fact]
    public void SelectInOrdinaryProcedure_NeverFiresTriggerOutput()
    {
        var findings = Scan("CREATE PROCEDURE dbo.P AS BEGIN SELECT 1; END");

        Assert.DoesNotContain(findings, f => f.Kind == ControlFlowRiskFindingKind.TriggerEmitsOutput);
    }

    // --- DirtyReadIsolationHint ---

    [Fact]
    public void NoLockHint_Fires()
    {
        var findings = Scan("SELECT A FROM dbo.T WITH (NOLOCK);");

        var finding = Assert.Single(findings, f => f.Kind == ControlFlowRiskFindingKind.DirtyReadIsolationHint);
        Assert.Equal(FindingConfidence.Low, finding.Confidence);
    }

    [Fact]
    public void ReadUncommittedHint_Fires()
    {
        var findings = Scan("SELECT A FROM dbo.T WITH (READUNCOMMITTED);");

        Assert.Contains(findings, f => f.Kind == ControlFlowRiskFindingKind.DirtyReadIsolationHint);
    }

    [Fact]
    public void SetIsolationLevelReadUncommitted_Fires()
    {
        var findings = Scan("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;");

        Assert.Contains(findings, f => f.Kind == ControlFlowRiskFindingKind.DirtyReadIsolationHint);
    }

    [Fact]
    public void SetIsolationLevelReadCommitted_NeverFires()
    {
        var findings = Scan("SET TRANSACTION ISOLATION LEVEL READ COMMITTED;");

        Assert.DoesNotContain(findings, f => f.Kind == ControlFlowRiskFindingKind.DirtyReadIsolationHint);
    }

    [Fact]
    public void NoTableHint_NeverFiresDirtyRead()
    {
        var findings = Scan("SELECT A FROM dbo.T;");

        Assert.DoesNotContain(findings, f => f.Kind == ControlFlowRiskFindingKind.DirtyReadIsolationHint);
    }

    // --- DuplicatedCallArgument ---

    [Fact]
    public void ExecWithSameVariablePassedTwice_Fires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                DECLARE @x INT = 1;
                EXEC dbo.Other @First = @x, @Second = @x;
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == ControlFlowRiskFindingKind.DuplicatedCallArgument);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void ExecWithDifferentArguments_NeverFires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                DECLARE @x INT = 1, @y INT = 2;
                EXEC dbo.Other @First = @x, @Second = @y;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == ControlFlowRiskFindingKind.DuplicatedCallArgument);
    }

    [Fact]
    public void ExecWithRepeatedNullLiteral_NeverFires()
    {
        var findings = Scan("EXEC dbo.Other @First = NULL, @Second = NULL;");

        Assert.DoesNotContain(findings, f => f.Kind == ControlFlowRiskFindingKind.DuplicatedCallArgument);
    }

    [Fact]
    public void FunctionCallWithSameColumnTwice_Fires()
    {
        var findings = Scan("SELECT dbo.SomeFunc(A, A) FROM dbo.T;");

        Assert.Contains(findings, f => f.Kind == ControlFlowRiskFindingKind.DuplicatedCallArgument);
    }

    [Fact]
    public void FormatMessageWithRepeatedArgument_NeverFires()
    {
        var findings = Scan("SELECT FORMATMESSAGE('%s and %s', A, A) FROM dbo.T;");

        Assert.DoesNotContain(findings, f => f.Kind == ControlFlowRiskFindingKind.DuplicatedCallArgument);
    }

    // --- LegacyIdentityIntrinsic ---

    [Fact]
    public void AtAtIdentityReference_Fires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                INSERT INTO dbo.T (A) VALUES (1);
                SELECT @@IDENTITY;
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == ControlFlowRiskFindingKind.LegacyIdentityIntrinsic);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void ScopeIdentityReference_NeverFires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                INSERT INTO dbo.T (A) VALUES (1);
                SELECT SCOPE_IDENTITY();
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == ControlFlowRiskFindingKind.LegacyIdentityIntrinsic);
    }

    // --- GotoUsage ---

    [Fact]
    public void Goto_Fires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                GOTO Done;
                SELECT 1;
                Done:
                SELECT 2;
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == ControlFlowRiskFindingKind.GotoUsage);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void NoGoto_NeverFires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                SELECT 1;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == ControlFlowRiskFindingKind.GotoUsage);
    }

    // --- CaseExpressionMissingElse ---

    [Fact]
    public void SimpleCaseWithNoElse_Fires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                DECLARE @x INT = 1;
                SELECT CASE @x WHEN 1 THEN 'a' WHEN 2 THEN 'b' END;
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == ControlFlowRiskFindingKind.CaseExpressionMissingElse);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void SimpleCaseWithElse_NeverFires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                DECLARE @x INT = 1;
                SELECT CASE @x WHEN 1 THEN 'a' WHEN 2 THEN 'b' ELSE 'c' END;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == ControlFlowRiskFindingKind.CaseExpressionMissingElse);
    }

    [Fact]
    public void SearchedCaseWithNoElse_NeverFiresMissingElse()
    {
        // Deliberately excluded - a searched CASE's boolean conditions are typically a
        // deliberately partial set, unlike a simple CASE's fixed, enumerable value list.
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                DECLARE @x INT = 1;
                SELECT CASE WHEN @x = 1 THEN 'a' WHEN @x = 2 THEN 'b' END;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == ControlFlowRiskFindingKind.CaseExpressionMissingElse);
    }

    // --- NonDeterministicCaseInput ---

    [Fact]
    public void NewIdAsSimpleCaseInput_Fires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                SELECT CASE NEWID()
                    WHEN '00000000-0000-0000-0000-000000000000' THEN 'a'
                    ELSE 'b'
                END;
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == ControlFlowRiskFindingKind.NonDeterministicCaseInput);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("NEWID", finding.DetailText);
    }

    [Fact]
    public void RandAsSimpleCaseInput_Fires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                SELECT CASE RAND() WHEN 0.5 THEN 'a' ELSE 'b' END;
            END
            """);

        Assert.Contains(findings, f => f.Kind == ControlFlowRiskFindingKind.NonDeterministicCaseInput);
    }

    [Fact]
    public void CryptGenRandomAsSimpleCaseInput_Fires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                SELECT CASE CRYPT_GEN_RANDOM(1) WHEN 0x00 THEN 'a' ELSE 'b' END;
            END
            """);

        Assert.Contains(findings, f => f.Kind == ControlFlowRiskFindingKind.NonDeterministicCaseInput);
    }

    [Fact]
    public void OrdinaryColumnAsSimpleCaseInput_NeverFiresNonDeterministic()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                SELECT CASE Status WHEN 1 THEN 'a' ELSE 'b' END FROM dbo.T;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == ControlFlowRiskFindingKind.NonDeterministicCaseInput);
    }

    [Fact]
    public void GetDateAsSimpleCaseInput_NeverFiresNonDeterministic()
    {
        // GETDATE() is deliberately out of scope for this kind - the checklist's own proposed
        // list is NEWID()/RAND()/CRYPT_GEN_RANDOM() only.
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS
            BEGIN
                SELECT CASE GETDATE() WHEN '2026-01-01' THEN 'a' ELSE 'b' END;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == ControlFlowRiskFindingKind.NonDeterministicCaseInput);
    }
}
