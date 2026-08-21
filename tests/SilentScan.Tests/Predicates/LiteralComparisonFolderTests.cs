using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class LiteralComparisonFolderTests
{
    [Theory]
    [InlineData("SELECT 1 WHERE 1 = 1;", true)]
    [InlineData("SELECT 1 WHERE 1 = 2;", false)]
    [InlineData("SELECT 1 WHERE 5 > 3;", true)]
    [InlineData("SELECT 1 WHERE 5 < 3;", false)]
    [InlineData("SELECT 1 WHERE 5 >= 5;", true)]
    [InlineData("SELECT 1 WHERE 5 <= 4;", false)]
    [InlineData("SELECT 1 WHERE 1 <> 2;", true)]
    [InlineData("SELECT 1 WHERE 1 <> 1;", false)]
    public void TryFoldComparison_NumericLiterals_FoldsToExpectedTruth(string sql, bool expected)
    {
        var comparison = ExtractComparison(sql);

        var result = LiteralComparisonFolder.TryFoldComparison(comparison.FirstExpression, comparison.SecondExpression, comparison.ComparisonType);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void TryFoldComparison_IdenticalStringLiterals_FoldsToTrue()
    {
        var comparison = ExtractComparison("SELECT 1 WHERE 'abc' = 'abc';");

        var result = LiteralComparisonFolder.TryFoldComparison(comparison.FirstExpression, comparison.SecondExpression, comparison.ComparisonType);

        Assert.True(result);
    }

    [Theory]
    [InlineData("SELECT 1 WHERE 'abc' = 'xyz';")]
    [InlineData("SELECT 1 WHERE 'abc' <> 'xyz';")]
    public void TryFoldComparison_DistinctStringLiterals_NeverFolds(string sql)
    {
        // A case-insensitive collation is not the risk here (the two strings are already
        // ordinally different) - but nothing pins down what collation-aware equality WOULD say
        // for two different strings in general, so this folder only ever asserts equality/
        // inequality of BYTE-IDENTICAL literals and declines everything else - see this type's
        // own doc comment on EvaluateExactStringMatch.
        var comparison = ExtractComparison(sql);

        var result = LiteralComparisonFolder.TryFoldComparison(comparison.FirstExpression, comparison.SecondExpression, comparison.ComparisonType);

        Assert.Null(result);
    }

    [Fact]
    public void TryFoldComparison_StringLiteralsDifferingOnlyByCase_DoesNotFold()
    {
        // A case-insensitive collation could make these compare equal at runtime - never guess.
        var comparison = ExtractComparison("SELECT 1 WHERE 'abc' = 'ABC';");

        var result = LiteralComparisonFolder.TryFoldComparison(comparison.FirstExpression, comparison.SecondExpression, comparison.ComparisonType);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("SELECT 1 WHERE 5 + 3 = 8;", true)]
    [InlineData("SELECT 1 WHERE 5 + 3 = 9;", false)]
    [InlineData("SELECT 1 WHERE 10 - 4 = 6;", true)]
    [InlineData("SELECT 1 WHERE 3 * 4 = 12;", true)]
    [InlineData("SELECT 1 WHERE 10 / 2 = 5;", true)]
    public void TryFoldComparison_OneLevelArithmeticOnOneSide_FoldsBeforeComparing(string sql, bool expected)
    {
        var comparison = ExtractComparison(sql);

        var result = LiteralComparisonFolder.TryFoldComparison(comparison.FirstExpression, comparison.SecondExpression, comparison.ComparisonType);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void TryFoldComparison_DivisionByZero_DoesNotFold()
    {
        var comparison = ExtractComparison("SELECT 1 WHERE 5 / 0 = 0;");

        var result = LiteralComparisonFolder.TryFoldComparison(comparison.FirstExpression, comparison.SecondExpression, comparison.ComparisonType);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("SELECT 1 WHERE NULL = 1;")]
    [InlineData("SELECT 1 WHERE 1 = NULL;")]
    [InlineData("SELECT 1 WHERE NULL = NULL;")]
    public void TryFoldComparison_EitherSideIsNull_NeverFolds(string sql)
    {
        var comparison = ExtractComparison(sql);

        var result = LiteralComparisonFolder.TryFoldComparison(comparison.FirstExpression, comparison.SecondExpression, comparison.ComparisonType);

        Assert.Null(result);
    }

    [Fact]
    public void TryFoldComparison_NonLiteralOperand_DoesNotFold()
    {
        var comparison = ExtractComparison("SELECT 1 WHERE Col = 1;");

        var result = LiteralComparisonFolder.TryFoldComparison(comparison.FirstExpression, comparison.SecondExpression, comparison.ComparisonType);

        Assert.Null(result);
    }

    [Fact]
    public void TryFoldToNumeric_DirectIntegerLiteral_ReturnsItsValue()
    {
        var comparison = ExtractComparison("SELECT 1 WHERE 42 = 42;");

        Assert.Equal(42m, LiteralComparisonFolder.TryFoldToNumeric(comparison.FirstExpression));
    }

    [Fact]
    public void TryFoldToNumeric_NullLiteral_ReturnsNull()
    {
        var comparison = ExtractComparison("SELECT 1 WHERE NULL = 1;");

        Assert.Null(LiteralComparisonFolder.TryFoldToNumeric(comparison.FirstExpression));
    }

    private static BooleanComparisonExpression ExtractComparison(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors);

        var visitor = new ComparisonFinder();
        result.Fragment.Accept(visitor);
        Assert.NotNull(visitor.Found);
        return visitor.Found!;
    }

    private sealed class ComparisonFinder : TSqlFragmentVisitor
    {
        public BooleanComparisonExpression? Found { get; private set; }

        public override void ExplicitVisit(BooleanComparisonExpression node) => Found ??= node;
    }
}
