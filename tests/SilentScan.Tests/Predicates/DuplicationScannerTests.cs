using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 4 "Dead and duplicated code" - the pattern-matching half.
/// Fully syntax-only, no oracle needed - see <see cref="DuplicationFinding"/>'s own doc comment
/// for the full scope/precision-guard rationale these tests exercise.
/// </summary>
public sealed class DuplicationScannerTests
{
    private static IReadOnlyList<DuplicationFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return DuplicationScanner.Scan(result);
    }

    // --- Commented-out code -------------------------------------------------

    [Fact]
    public void CommentContainingRealStatement_FiresCommentedOutCode()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                -- SELECT Id, Name FROM dbo.Customer WHERE Active = 1
                SELECT 1;
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DuplicationFindingKind.CommentedOutCode);
        Assert.Equal(FindingConfidence.Low, finding.Confidence);
    }

    [Fact]
    public void BlockCommentContainingRealStatement_FiresCommentedOutCode()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                /* UPDATE dbo.Customer SET Active = 0 WHERE Id = 1; */
                SELECT 1;
            END
            """);

        Assert.Contains(findings, f => f.Kind == DuplicationFindingKind.CommentedOutCode);
    }

    [Fact]
    public void ProseComment_NeverFiresCommentedOutCode()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                -- This procedure handles the nightly customer reconciliation job.
                SELECT 1;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.CommentedOutCode);
    }

    [Fact]
    public void ShortComment_NeverFiresCommentedOutCode()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                -- TODO fix
                SELECT 1;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.CommentedOutCode);
    }

    [Fact]
    public void TwoWordProseComment_NeverFiresCommentedOutCode()
    {
        // Regression guard for a real false positive found against the local test database:
        // T-SQL's grammar accepts EXEC being omitted the moment a bare identifier appears where a
        // statement is expected, so "word1 word2" alone reparses cleanly as an implicit
        // "EXECUTE word1 word2" - an ordinary two-word annotation comment like this one must never
        // be mistaken for commented-out code just because it happens to satisfy that shorthand.
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                SELECT SettingValue FROM dbo.Settings WHERE Id = 43 /* Distance Factor */;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.CommentedOutCode);
    }

    // --- Duplicated string literal ------------------------------------------

    [Fact]
    public void StringLiteralRepeatedThreeTimes_FiresDuplicatedStringLiteral()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @a VARCHAR(20) = 'PENDING_REVIEW';
                DECLARE @b VARCHAR(20) = 'PENDING_REVIEW';
                DECLARE @c VARCHAR(20) = 'PENDING_REVIEW';
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DuplicationFindingKind.DuplicatedStringLiteral);
        Assert.Equal(FindingConfidence.Low, finding.Confidence);
    }

    [Fact]
    public void StringLiteralRepeatedTwice_NeverFiresDuplicatedStringLiteral()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @a VARCHAR(20) = 'PENDING_REVIEW';
                DECLARE @b VARCHAR(20) = 'PENDING_REVIEW';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.DuplicatedStringLiteral);
    }

    [Fact]
    public void ShortLiteralRepeatedManyTimes_NeverFiresDuplicatedStringLiteral()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @a CHAR(1) = 'Y';
                DECLARE @b CHAR(1) = 'Y';
                DECLARE @c CHAR(1) = 'Y';
                DECLARE @d CHAR(1) = 'Y';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.DuplicatedStringLiteral);
    }

    [Fact]
    public void NationalAndNonNationalLiteral_TrackedSeparately()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @a VARCHAR(20) = 'PENDING_REVIEW';
                DECLARE @b NVARCHAR(20) = N'PENDING_REVIEW';
                DECLARE @c VARCHAR(20) = 'PENDING_REVIEW';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.DuplicatedStringLiteral);
    }

    // --- Single-iteration loop -----------------------------------------------

    [Fact]
    public void WhileBodyAlwaysBreaks_FiresSingleIterationLoop()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @i INT = 0;
                WHILE @i < 10
                BEGIN
                    SET @i = @i + 1;
                    BREAK;
                END
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DuplicationFindingKind.SingleIterationLoop);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void WhileBodyConditionallyBreaks_NeverFiresSingleIterationLoop()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @i INT = 0;
                WHILE @i < 10
                BEGIN
                    SET @i = @i + 1;
                    IF @i > 5 BREAK;
                END
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.SingleIterationLoop);
    }

    [Fact]
    public void WhileBodyNeverBreaks_NeverFiresSingleIterationLoop()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @i INT = 0;
                WHILE @i < 10
                BEGIN
                    SET @i = @i + 1;
                END
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.SingleIterationLoop);
    }

    [Fact]
    public void NestedWhileOwnBreak_NeverCountsTowardOuterLoop()
    {
        // The inner loop's own BREAK is conditional (so the inner loop itself correctly never
        // fires either) - this isolates the one fact under test: an inner loop's BREAK must never
        // be mistaken for the outer loop's own unconditional exit.
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @i INT = 0;
                DECLARE @j INT = 0;
                WHILE @i < 10
                BEGIN
                    SET @i = @i + 1;
                    WHILE @j < 10
                    BEGIN
                        SET @j = @j + 1;
                        IF @j > 5 BREAK;
                    END
                END
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.SingleIterationLoop);
    }

    [Fact]
    public void WhileBodyWithGoto_DeclinesSingleIterationLoopAnalysis()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @i INT = 0;
                WHILE @i < 10
                BEGIN
                    SET @i = @i + 1;
                    BREAK;
                    GoToLabel:
                    PRINT 'x';
                END
                GOTO GoToLabel;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.SingleIterationLoop);
    }

    // --- Self-assignment -------------------------------------------------

    [Fact]
    public void SetVariableToItself_FiresSelfAssignment()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                SET @x = @x;
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DuplicationFindingKind.SelfAssignment);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void SelectSetVariableToItself_FiresSelfAssignment()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                SELECT @x = @x;
            END
            """);

        Assert.Contains(findings, f => f.Kind == DuplicationFindingKind.SelfAssignment);
    }

    [Fact]
    public void SetVariableToDifferentExpression_NeverFiresSelfAssignment()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @y INT = 2;
                SET @x = @y + 1;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.SelfAssignment);
    }

    [Fact]
    public void UpdateColumnToItself_FiresSelfAssignment()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                UPDATE dbo.T SET Col = Col WHERE Id = 1;
            END
            """);

        Assert.Contains(findings, f => f.Kind == DuplicationFindingKind.SelfAssignment);
    }

    [Fact]
    public void MultiTableUpdateDifferentAlias_NeverFiresSelfAssignment()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                UPDATE t SET t.Col = s.Col FROM dbo.T t JOIN dbo.S s ON t.Id = s.Id;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.SelfAssignment);
    }

    [Fact]
    public void CompoundAssignment_NeverFiresSelfAssignment()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                SET @x += 1;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.SelfAssignment);
    }

    // --- Identical binary operands -----------------------------------------

    [Fact]
    public void ComparisonWithIdenticalOperands_FiresIdenticalBinaryOperands()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                IF @x = @x PRINT 'x';
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DuplicationFindingKind.IdenticalBinaryOperands);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void LogicalAndWithIdenticalOperands_FiresIdenticalBinaryOperands()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @y INT = 2;
                IF @x = @y AND @x = @y PRINT 'x';
            END
            """);

        Assert.Contains(findings, f => f.Kind == DuplicationFindingKind.IdenticalBinaryOperands);
    }

    [Fact]
    public void SubtractWithIdenticalOperands_FiresIdenticalBinaryOperands()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @y INT = @x - @x;
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DuplicationFindingKind.IdenticalBinaryOperands);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void AddWithIdenticalOperands_NeverFiresIdenticalBinaryOperands()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @y INT = @x + @x;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.IdenticalBinaryOperands);
    }

    [Fact]
    public void MultiplyWithIdenticalOperands_NeverFiresIdenticalBinaryOperands()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @y INT = @x * @x;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.IdenticalBinaryOperands);
    }

    [Fact]
    public void LiteralOneEqualsLiteralOne_NeverFiresIdenticalBinaryOperands()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                IF 1 = 1 PRINT 'placeholder';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.IdenticalBinaryOperands);
    }

    [Fact]
    public void DifferentOperands_NeverFiresIdenticalBinaryOperands()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @y INT = 2;
                IF @x = @y PRINT 'x';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.IdenticalBinaryOperands);
    }

    // --- Repeated unary operator ---------------------------------------------

    [Fact]
    public void DoubleNegation_FiresRepeatedUnaryOperator()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x BIT = 1;
                IF NOT (NOT (@x = 1)) PRINT 'x';
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DuplicationFindingKind.RepeatedUnaryOperator);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void DoubleUnaryMinus_FiresRepeatedUnaryOperator()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @y INT = - - @x;
            END
            """);

        Assert.Contains(findings, f => f.Kind == DuplicationFindingKind.RepeatedUnaryOperator);
    }

    [Fact]
    public void SingleNegation_NeverFiresRepeatedUnaryOperator()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x BIT = 1;
                IF NOT (@x = 1) PRINT 'x';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.RepeatedUnaryOperator);
    }

    // --- Negated comparison as opposite -------------------------------------

    [Fact]
    public void NotGreaterThan_FiresNegatedComparisonAsOpposite()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @y INT = 2;
                IF NOT (@x > @y) PRINT 'x';
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DuplicationFindingKind.NegatedComparisonAsOpposite);
        Assert.Equal("<=", finding.DetailText);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void NotEquals_FiresNegatedComparisonAsOpposite()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @y INT = 2;
                IF NOT (@x = @y) PRINT 'x';
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DuplicationFindingKind.NegatedComparisonAsOpposite);
        Assert.Equal("<>", finding.DetailText);
    }

    [Fact]
    public void NotIsNull_FiresNegatedComparisonAsOpposite()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = NULL;
                IF NOT (@x IS NULL) PRINT 'x';
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DuplicationFindingKind.NegatedComparisonAsOpposite);
        Assert.Equal("IS NOT NULL", finding.DetailText);
    }

    [Fact]
    public void IsNotNullDirectly_NeverFiresNegatedComparisonAsOpposite()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = NULL;
                IF @x IS NOT NULL PRINT 'x';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.NegatedComparisonAsOpposite);
    }

    [Fact]
    public void PlainComparisonNoNegation_NeverFiresNegatedComparisonAsOpposite()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @y INT = 2;
                IF @x <= @y PRINT 'x';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.NegatedComparisonAsOpposite);
    }

    [Fact]
    public void NegatedAndExpression_NeverFiresNegatedComparisonAsOpposite()
    {
        // NOT (a AND b) is De Morgan's, a different shape entirely - not this rule's territory.
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @y INT = 2;
                IF NOT (@x = 1 AND @y = 2) PRINT 'x';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.NegatedComparisonAsOpposite);
    }
}
