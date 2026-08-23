using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class DuplicationScannerTests
{
    private static IReadOnlyList<DuplicationFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return DuplicationScanner.Scan(result, CatalogBuilder.Build([result]));
    }

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

        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                SELECT SettingValue FROM dbo.Settings WHERE Id = 43 /* Distance Factor */;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.CommentedOutCode);
    }

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

    [Fact]
    public void ColumnEqualsItself_ProvenNotNull_FiresIdenticalBinaryOperands()
    {

        var findings = Scan("""
            CREATE TABLE dbo.Orders (Code VARCHAR(20) NOT NULL);
            GO
            SELECT 1 FROM dbo.Orders WHERE Code = Code;
            """);

        var finding = Assert.Single(findings, f => f.Kind == DuplicationFindingKind.IdenticalBinaryOperands);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void ColumnEqualsItself_Nullable_NeverFiresIdenticalBinaryOperands()
    {

        var findings = Scan("""
            CREATE TABLE dbo.Orders (Code VARCHAR(20) NULL);
            GO
            SELECT 1 FROM dbo.Orders WHERE Code = Code;
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.IdenticalBinaryOperands);
    }

    [Fact]
    public void ColumnEqualsItself_UnresolvableAgainstAmbiguousScope_NeverFiresIdenticalBinaryOperands()
    {

        var findings = Scan("""
            CREATE TABLE dbo.Orders (Code VARCHAR(20) NOT NULL);
            CREATE TABLE dbo.Archive (Code VARCHAR(20) NOT NULL);
            GO
            SELECT 1 FROM dbo.Orders, dbo.Archive WHERE Code = Code;
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.IdenticalBinaryOperands);
    }

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

        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @y INT = 2;
                IF NOT (@x = 1 AND @y = 2) PRINT 'x';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.NegatedComparisonAsOpposite);
    }

    [Fact]
    public void RepeatedElseIfCondition_FiresDuplicateSiblingCondition()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                IF @x = 1 PRINT 'a';
                ELSE IF @x = 2 PRINT 'b';
                ELSE IF @x = 1 PRINT 'c';
            END
            """);

        Assert.Contains(findings, f => f.Kind == DuplicationFindingKind.DuplicateSiblingCondition);
    }

    [Fact]
    public void DistinctElseIfConditions_NeverFireDuplicateSiblingCondition()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                IF @x = 1 PRINT 'a';
                ELSE IF @x = 2 PRINT 'b';
                ELSE IF @x = 3 PRINT 'c';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.DuplicateSiblingCondition);
    }

    [Fact]
    public void RepeatedCaseWhenCondition_FiresDuplicateSiblingCondition()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @y INT;
                SET @y = CASE WHEN @x = 1 THEN 10 WHEN @x = 2 THEN 20 WHEN @x = 1 THEN 30 END;
            END
            """);

        Assert.Contains(findings, f => f.Kind == DuplicationFindingKind.DuplicateSiblingCondition);
    }

    [Fact]
    public void TwoOfThreeIfBranchesIdentical_FiresIdenticalBranchBodies()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                IF @x = 1 PRINT 'same';
                ELSE IF @x = 2 PRINT 'different';
                ELSE PRINT 'same';
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DuplicationFindingKind.IdenticalBranchBodies);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.AllBranchesIdentical);
    }

    [Fact]
    public void AllIfElseBranchesIdentical_FiresAllBranchesIdenticalNotPartial()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                IF @x = 1 PRINT 'same';
                ELSE IF @x = 2 PRINT 'same';
                ELSE PRINT 'same';
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DuplicationFindingKind.AllBranchesIdentical);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.IdenticalBranchBodies);
    }

    [Fact]
    public void IfChainWithNoElse_NeverFiresAllBranchesIdentical()
    {

        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                IF @x = 1 PRINT 'same';
                ELSE IF @x = 2 PRINT 'same';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.AllBranchesIdentical);
    }

    [Fact]
    public void DistinctBranchBodies_NeverFireIdenticalBranchBodies()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                IF @x = 1 PRINT 'a';
                ELSE PRINT 'b';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.IdenticalBranchBodies);
        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.AllBranchesIdentical);
    }

    [Fact]
    public void AllCaseWhenBranchesIdenticalWithElse_FiresAllBranchesIdentical()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @y INT;
                SET @y = CASE WHEN @x = 1 THEN 5 WHEN @x = 2 THEN 5 ELSE 5 END;
            END
            """);

        Assert.Contains(findings, f => f.Kind == DuplicationFindingKind.AllBranchesIdentical);
    }

    [Fact]
    public void UnbracedNestedIfWithNoElseEither_FiresCollapsibleNestedIf()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @y INT = 2;
                IF @x = 1
                    IF @y = 2 PRINT 'both';
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DuplicationFindingKind.CollapsibleNestedIf);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void BracedNestedIfWithNoElseEither_FiresCollapsibleNestedIf()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @y INT = 2;
                IF @x = 1
                BEGIN
                    IF @y = 2 PRINT 'both';
                END
            END
            """);

        Assert.Contains(findings, f => f.Kind == DuplicationFindingKind.CollapsibleNestedIf);
    }

    [Fact]
    public void OuterIfHasElse_NeverFiresCollapsibleNestedIf()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @y INT = 2;
                IF @x = 1
                    IF @y = 2 PRINT 'both';
                ELSE
                    PRINT 'not x';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.CollapsibleNestedIf);
    }

    [Fact]
    public void InnerNestedIfHasElse_NeverFiresCollapsibleNestedIf()
    {

        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @y INT = 2;
                IF @x = 1
                    IF @y = 2 PRINT 'both';
                    ELSE PRINT 'only x';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.CollapsibleNestedIf);
    }

    [Fact]
    public void NestedIfBesideOtherStatements_NeverFiresCollapsibleNestedIf()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @y INT = 2;
                IF @x = 1
                BEGIN
                    PRINT 'entering';
                    IF @y = 2 PRINT 'both';
                END
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.CollapsibleNestedIf);
    }

    [Fact]
    public void IIfNestedInThenBranch_FiresNestedConditionalExpression()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @y INT = 2;
                DECLARE @z INT;
                SET @z = IIF(@x = 1, IIF(@y = 2, 10, 20), 30);
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DuplicationFindingKind.NestedConditionalExpression);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void IIfNestedInElseBranch_FiresNestedConditionalExpression()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @y INT = 2;
                DECLARE @z INT;
                SET @z = IIF(@x = 1, 10, IIF(@y = 2, 20, 30));
            END
            """);

        Assert.Contains(findings, f => f.Kind == DuplicationFindingKind.NestedConditionalExpression);
    }

    [Fact]
    public void FlatIIfNoNesting_NeverFiresNestedConditionalExpression()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @z INT;
                SET @z = IIF(@x = 1, 10, 20);
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.NestedConditionalExpression);
    }

    [Fact]
    public void CaseNestedInCase_NeverFiresNestedConditionalExpression()
    {

        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 1;
                DECLARE @y INT = 2;
                DECLARE @z INT;
                SET @z = CASE WHEN @x = 1 THEN CASE WHEN @y = 2 THEN 10 ELSE 20 END ELSE 30 END;
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.NestedConditionalExpression);
    }

    [Fact]
    public void LooserBoundAndedWithStricterBound_FiresRedundantAndCondition()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 10;
                IF @x > 5 AND @x > 3 PRINT 'y';
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DuplicationFindingKind.RedundantAndCondition);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void ContradictoryBoundsAnded_FiresMutuallyExclusiveAndCondition()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 10;
                IF @x > 10 AND @x < 5 PRINT 'y';
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DuplicationFindingKind.MutuallyExclusiveAndCondition);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void AdjacentTouchingBoundsExclusiveAndInclusive_FiresMutuallyExclusiveAndCondition()
    {

        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 10;
                IF @x > 5 AND @x <= 5 PRINT 'y';
            END
            """);

        Assert.Contains(findings, f => f.Kind == DuplicationFindingKind.MutuallyExclusiveAndCondition);
    }

    [Fact]
    public void NarrowingBoundsAnded_NeverFireEither()
    {

        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 10;
                IF @x > 3 AND @x < 100 PRINT 'y';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.RedundantAndCondition);
        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.MutuallyExclusiveAndCondition);
    }

    [Fact]
    public void BoundsOnDifferentOperands_NeverFireEither()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 10;
                DECLARE @y INT = 1;
                IF @x > 5 AND @y > 3 PRINT 'y';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.RedundantAndCondition);
        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.MutuallyExclusiveAndCondition);
    }

    [Fact]
    public void OrCombinedBounds_NeverFireEither()
    {

        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 10;
                IF @x > 10 OR @x < 5 PRINT 'y';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.RedundantAndCondition);
        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.MutuallyExclusiveAndCondition);
    }

    [Fact]
    public void RedundantAndConditionAlsoDetectedInWhileLoop()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 10;
                WHILE @x > 5 AND @x > 3
                BEGIN
                    SET @x -= 1;
                END
            END
            """);

        Assert.Contains(findings, f => f.Kind == DuplicationFindingKind.RedundantAndCondition);
    }

    [Fact]
    public void DifferentIntegerLiteralsAlwaysFalseEquality_FiresAlwaysTrueOrFalse()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                IF 1 = 0 PRINT 'never';
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DuplicationFindingKind.AlwaysTrueOrFalseLiteralComparison);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Equal("always false", finding.DetailText);
    }

    [Fact]
    public void NumericLiteralOrderingAlwaysTrue_FiresAlwaysTrueOrFalse()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                IF 5 > 1 PRINT 'always';
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DuplicationFindingKind.AlwaysTrueOrFalseLiteralComparison);
        Assert.Equal("always true", finding.DetailText);
    }

    [Fact]
    public void IdenticalNumericLiteralsEquality_FiresAlwaysTrueOrFalseNotIdenticalOperands()
    {

        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                IF 5 = 5 PRINT 'always';
            END
            """);

        Assert.Contains(findings, f => f.Kind == DuplicationFindingKind.AlwaysTrueOrFalseLiteralComparison);
        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.IdenticalBinaryOperands);
    }

    [Fact]
    public void IdenticalStringLiteralsEquality_FiresAlwaysTrueOrFalse()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                IF 'abc' = 'abc' PRINT 'always';
            END
            """);

        var finding = Assert.Single(findings, f => f.Kind == DuplicationFindingKind.AlwaysTrueOrFalseLiteralComparison);
        Assert.Equal("always true", finding.DetailText);
    }

    [Fact]
    public void DifferentStringLiteralsEquality_NeverFiresAlwaysTrueOrFalse()
    {

        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                IF 'abc' = 'ABC' PRINT 'maybe';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.AlwaysTrueOrFalseLiteralComparison);
    }

    [Fact]
    public void DifferentStringLiteralsNotEqual_NeverFiresAlwaysTrueOrFalse()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                IF 'abc' <> 'xyz' PRINT 'maybe';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.AlwaysTrueOrFalseLiteralComparison);
    }

    [Fact]
    public void LiteralComparedAgainstColumn_NeverFiresAlwaysTrueOrFalse()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                DECLARE @x INT = 5;
                IF @x = 5 PRINT 'maybe';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.AlwaysTrueOrFalseLiteralComparison);
    }

    [Fact]
    public void MixedTypeLiteralComparison_NeverFiresAlwaysTrueOrFalse()
    {

        var findings = Scan("""
            CREATE PROCEDURE dbo.P AS BEGIN
                IF 5 = 'x' PRINT 'maybe';
            END
            """);

        Assert.DoesNotContain(findings, f => f.Kind == DuplicationFindingKind.AlwaysTrueOrFalseLiteralComparison);
    }
}
