using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;

namespace SilentScan.Tests.Predicates.DynamicSqlValue;

public sealed class ExpressionEvaluatorTests
{
    private const int Cap = 32;
    private const string SourcePath = "test.sql";

    private static ScalarExpression ParseExpression(string sql)
    {
        var result = SqlScriptParser.ParseText(SourcePath, $"SELECT {sql}");
        Assert.False(result.HasErrors, string.Join(';', result.Errors.Select(e => e.Message)));
        var script = Assert.IsType<TSqlScript>(result.Fragment);
        var select = Assert.IsType<SelectStatement>(script.Batches[0].Statements[0]);
        var querySpec = Assert.IsType<QuerySpecification>(select.QueryExpression);
        var selectElement = Assert.IsType<SelectScalarExpression>(querySpec.SelectElements[0]);
        return selectElement.Expression;
    }

    private static SqlTextValue Fold(string sql, Dictionary<string, SqlTextValue>? state = null) =>
        ExpressionEvaluator.Fold(ParseExpression(sql), state ?? new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase), SourcePath, Cap);

    private static string LitText(SqlTextValue value)
    {
        var template = Assert.IsType<SqlTextValue.Template>(value);
        var lit = Assert.IsType<TemplatePiece.Lit>(Assert.Single(template.Pieces));
        return lit.Text;
    }

    private static string TaintReason(SqlTextValue value) => Assert.IsType<SqlTextValue.Tainted>(value).Reason;

private static string FlattenLitText(SqlTextValue value)
    {
        var template = Assert.IsType<SqlTextValue.Template>(value);
        return string.Concat(template.Pieces.Select(p => Assert.IsType<TemplatePiece.Lit>(p).Text));
    }

    [Fact]
    public void StringLiteral_FoldsToLiteralText()
    {
        Assert.Equal("hello", LitText(Fold("'hello'")));
    }

    [Fact]
    public void NationalStringLiteral_TracksTwoCharacterPrefix()
    {
        var template = Assert.IsType<SqlTextValue.Template>(Fold("N'hello'"));
        var lit = Assert.IsType<TemplatePiece.Lit>(Assert.Single(template.Pieces));
        Assert.Equal(2, lit.PrefixLength);
    }

    [Fact]
    public void VariableReference_KnownInState_ReturnsItsValue()
    {
        var state = new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["@x"] = new SqlTextValue.Template([new TemplatePiece.Lit("known", new SourceSpan(SourcePath, 1, 1), 1)]),
        };

        Assert.Equal("known", LitText(Fold("@x", state)));
    }

    [Fact]
    public void VariableReference_UnknownInState_TaintsVariableNotInScope()
    {
        Assert.Equal("variable-not-in-scope", TaintReason(Fold("@unknown")));
    }

    [Fact]
    public void Concatenation_TwoLiterals_JoinsText()
    {
        Assert.Equal("ab", FlattenLitText(Fold("'a' + 'b'")));
    }

    [Fact]
    public void Concatenation_TaintedLeftOperandWithNoAlternatives_KeepsItsOwnReason()
    {
        Assert.Equal("variable-not-in-scope", TaintReason(Fold("@unknown + 'b'")));
    }

    [Fact]
    public void UnsupportedBinaryOperator_Declines()
    {
        Assert.Equal("non-literal-expression:unsupported-operator", TaintReason(Fold("1 * 2")));
    }

    [Fact]
    public void FunctionCall_Upper_DispatchesToRegistry()
    {
        Assert.Equal("ABC", LitText(Fold("UPPER('abc')")));
    }

    [Fact]
    public void FunctionCall_Substring_FoldsIntegerArgumentsAndDispatches()
    {
        Assert.Equal("bcd", LitText(Fold("SUBSTRING('abcdef', 2, 3)")));
    }

    [Fact]
    public void FunctionCall_Substring_IntegerArgumentFromVariable_DeclinesFunctionCallArgumentDiverges()
    {
        Assert.Equal("non-literal-expression:function-call-argument-diverges", TaintReason(Fold("SUBSTRING('abcdef', @start, 3)")));
    }

    [Fact]
    public void FunctionCall_Left_UsesDedicatedNodeType()
    {
        Assert.Equal("abc", LitText(Fold("LEFT('abcdef', 3)")));
    }

    [Fact]
    public void FunctionCall_Right_UsesDedicatedNodeType()
    {
        Assert.Equal("def", LitText(Fold("RIGHT('abcdef', 3)")));
    }

    [Fact]
    public void FunctionCall_Char_IntegerArgumentPosition_FoldsCodePoint()
    {
        Assert.Equal("A", LitText(Fold("CHAR(65)")));
    }

    [Fact]
    public void Cast_VarCharTarget_TruncatesOverLengthLiteral()
    {
        Assert.Equal("abc", LitText(Fold("CAST('abcdef' AS VARCHAR(3))")));
    }

    [Fact]
    public void Convert_VarCharTarget_TruncatesOverLengthLiteral()
    {
        Assert.Equal("abc", LitText(Fold("CONVERT(VARCHAR(3), 'abcdef')")));
    }

    [Fact]
    public void Cast_NonStringTarget_DeclinesCastTargetNotPinned()
    {
        Assert.Equal("non-literal-expression:cast-target-not-pinned", TaintReason(Fold("CAST('42' AS INT)")));
    }

    [Fact]
    public void IsNull_FirstArgumentFolds_ReturnsItWithoutInspectingSecond()
    {
        Assert.Equal("a", LitText(Fold("ISNULL('a', @unknown)")));
    }

    [Fact]
    public void IsNull_FirstArgumentDoesNotFold_TaintsFromFirstArgument()
    {
        Assert.Equal("variable-not-in-scope", TaintReason(Fold("ISNULL(@unknown, 'b')")));
    }

    [Fact]
    public void Coalesce_FirstArgumentFolds_ReturnsItWithoutInspectingRest()
    {
        Assert.Equal("a", LitText(Fold("COALESCE('a', @unknown, @alsoUnknown)")));
    }

    [Fact]
    public void SearchedCase_AllBranchesFold_UnionsAsChoice()
    {
        var result = (SqlTextValue.Template)Fold("CASE WHEN 1 = 1 THEN 'a' WHEN 2 = 2 THEN 'b' ELSE 'c' END");

        var choice = Assert.IsType<TemplatePiece.Choice>(Assert.Single(result.Pieces));
        var texts = choice.Alternatives.Select(LitText).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(["a", "b", "c"], texts);
    }

    [Fact]
    public void SearchedCase_NoElse_DeclinesConditional()
    {
        Assert.Equal("non-literal-expression:conditional", TaintReason(Fold("CASE WHEN 1 = 1 THEN 'a' END")));
    }

    [Fact]
    public void SearchedCase_OneBranchDoesNotFold_DeclinesWithThatBranchsOwnReason()
    {
        Assert.Equal("variable-not-in-scope", TaintReason(Fold("CASE WHEN 1 = 1 THEN @unknown ELSE 'c' END")));
    }

    [Fact]
    public void SearchedCase_OneBranchDoesNotFold_PreservesTheKnownBranchAsGuardedAlternative()
    {
        var tainted = Assert.IsType<SqlTextValue.Tainted>(Fold("CASE WHEN 1 = 1 THEN @unknown ELSE 'c' END"));

        var alternative = Assert.Single(tainted.GuardedAlternatives!);
        var text = Assert.Single(alternative.Value.Pieces);
        Assert.Equal("c", Assert.IsType<TemplatePiece.Lit>(text).Text);
    }

    [Fact]
    public void ColumnReference_Declines()
    {
        Assert.Equal("non-literal-expression:column-reference", TaintReason(Fold("SomeColumn")));
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("-5", -5)]
    [InlineData("+5", 5)]
    [InlineData("(5)", 5)]
    [InlineData("2 + 3", 5)]
    [InlineData("7 - 2", 5)]
    [InlineData("LEN('abcde')", 5)]
    [InlineData("LEN('abc  ')", 3)]
    public void FoldInteger_HandlesLiteralArithmeticAndLen(string sql, int expected)
    {
        Assert.True(ExpressionEvaluator.FoldInteger(ParseExpression(sql), new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase), SourcePath, Cap, out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void FoldInteger_VariableReference_NeverTracked_Declines()
    {
        Assert.False(ExpressionEvaluator.FoldInteger(ParseExpression("@n"), new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase), SourcePath, Cap, out _));
    }
}
