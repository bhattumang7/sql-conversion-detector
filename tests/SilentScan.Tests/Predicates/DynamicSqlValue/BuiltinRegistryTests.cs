using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates.DynamicSqlValue;

public sealed class BuiltinRegistryTests
{
    private static readonly SourceSpan Site = new("test.sql", 10, 5);
    private static readonly SqlType NVarChar50 = new(SqlTypeCategory.NVarChar, Length: 50);

    private static BuiltinCall Call(string name, params BuiltinArgument[] args) => new(name, args, Site);

    private static string OkText(BuiltinFoldResult result)
    {
        var ok = Assert.IsType<BuiltinFoldResult.Ok>(result);
        var lit = Assert.IsType<TemplatePiece.Lit>(Assert.Single(ok.Pieces));
        return lit.Text;
    }

    private static string FailReason(BuiltinFoldResult result) => Assert.IsType<BuiltinFoldResult.Fail>(result).Reason;

    private static TemplatePiece.Hole OkHole(BuiltinFoldResult result)
    {
        var ok = Assert.IsType<BuiltinFoldResult.Ok>(result);
        return Assert.IsType<TemplatePiece.Hole>(Assert.Single(ok.Pieces));
    }

    [Theory]
    [InlineData("UPPER", "abc", "ABC")]
    [InlineData("LOWER", "ABC", "abc")]
    public void CaseConversion_AsciiInput_Converts(string function, string input, string expected)
    {
        var result = BuiltinRegistry.Fold(Call(function, new BuiltinArgument.Text(input)));

        Assert.Equal(expected, OkText(result));
    }

    [Theory]
    [InlineData("UPPER", "abci")]
    [InlineData("LOWER", "ABCI")]
    [InlineData("UPPER", "café")]
    public void CaseConversion_UnsafeInput_DeclinesCollationSensitive(string function, string input)
    {
        var result = BuiltinRegistry.Fold(Call(function, new BuiltinArgument.Text(input)));

        Assert.Equal("non-literal-expression:case-conversion-collation-sensitive", FailReason(result));
    }

    [Fact]
    public void CaseConversion_HoleArgument_PassesThroughSameTypeAndKind()
    {
        var result = BuiltinRegistry.Fold(Call("UPPER", new BuiltinArgument.Hole(NVarChar50, HoleKind.UninitializedDeclare)));

        var hole = OkHole(result);
        Assert.Equal(NVarChar50, hole.Type);
        Assert.Equal(HoleKind.UninitializedDeclare, hole.Kind);
    }

    [Theory]
    [InlineData("LTRIM", "  abc", "abc")]
    [InlineData("RTRIM", "abc  ", "abc")]
    public void Trim_OnlyTrimsSpaceCharacter(string function, string input, string expected)
    {
        var result = BuiltinRegistry.Fold(Call(function, new BuiltinArgument.Text(input)));

        Assert.Equal(expected, OkText(result));
    }

    [Fact]
    public void Trim_DoesNotTrimTabs()
    {
        var result = BuiltinRegistry.Fold(Call("LTRIM", new BuiltinArgument.Text("\tabc")));

        Assert.Equal("\tabc", OkText(result));
    }

    [Theory]
    [InlineData("LEFT", "abcdef", 3, "abc")]
    [InlineData("LEFT", "ab", 5, "ab")]
    [InlineData("RIGHT", "abcdef", 3, "def")]
    [InlineData("RIGHT", "ab", 5, "ab")]
    public void LeftRight_ClampsLengthToInputSize(string function, string input, int length, string expected)
    {
        var result = BuiltinRegistry.Fold(Call(function, new BuiltinArgument.Text(input), new BuiltinArgument.Number(length)));

        Assert.Equal(expected, OkText(result));
    }

    [Theory]
    [InlineData("LEFT")]
    [InlineData("RIGHT")]
    public void LeftRight_NegativeLength_Declines(string function)
    {
        var result = BuiltinRegistry.Fold(Call(function, new BuiltinArgument.Text("abc"), new BuiltinArgument.Number(-1)));

        Assert.Equal("non-literal-expression:negative-length", FailReason(result));
    }

    [Fact]
    public void LeftRight_HoleSource_PassesThroughType()
    {
        var result = BuiltinRegistry.Fold(Call("LEFT", new BuiltinArgument.Hole(NVarChar50, HoleKind.UntypedParameter), new BuiltinArgument.Number(3)));

        Assert.Equal(NVarChar50, OkHole(result).Type);
    }

    [Fact]
    public void LeftRight_HoleSourceWithNegativeLength_StillDeclinesNegativeLength()
    {
        var result = BuiltinRegistry.Fold(Call("LEFT", new BuiltinArgument.Hole(NVarChar50, HoleKind.UntypedParameter), new BuiltinArgument.Number(-1)));

        Assert.Equal("non-literal-expression:negative-length", FailReason(result));
    }

    [Theory]
    [InlineData("abcdef", 2, 3, "bcd")]
    [InlineData("abcdef", 2, 100, "bcdef")]
    [InlineData("abc", 10, 2, "")]
    public void Substring_ClampsAndHandlesOutOfRangeStart(string input, int start, int length, string expected)
    {
        var result = BuiltinRegistry.Fold(Call("SUBSTRING", new BuiltinArgument.Text(input), new BuiltinArgument.Number(start), new BuiltinArgument.Number(length)));

        Assert.Equal(expected, OkText(result));
    }

    [Fact]
    public void Substring_NegativeLength_Declines()
    {
        var result = BuiltinRegistry.Fold(Call("SUBSTRING", new BuiltinArgument.Text("abc"), new BuiltinArgument.Number(1), new BuiltinArgument.Number(-1)));

        Assert.Equal("non-literal-expression:negative-length", FailReason(result));
    }

    [Fact]
    public void Substring_StartBelowOne_Declines()
    {
        var result = BuiltinRegistry.Fold(Call("SUBSTRING", new BuiltinArgument.Text("abc"), new BuiltinArgument.Number(0), new BuiltinArgument.Number(2)));

        Assert.Equal("non-literal-expression:substring-start-below-one", FailReason(result));
    }

    [Fact]
    public void Substring_HoleSource_PassesThroughType()
    {
        var result = BuiltinRegistry.Fold(Call("SUBSTRING", new BuiltinArgument.Hole(NVarChar50, HoleKind.UntypedParameter), new BuiltinArgument.Number(1), new BuiltinArgument.Number(3)));

        Assert.Equal(NVarChar50, OkHole(result).Type);
    }

    [Fact]
    public void Replace_AllLiteral_ReplacesWhenCollationInsensitive()
    {
        var result = BuiltinRegistry.Fold(Call(
            "REPLACE", new BuiltinArgument.Text("a-b-c"), new BuiltinArgument.Text("-"), new BuiltinArgument.Text("_")));

        Assert.Equal("a_b_c", OkText(result));
    }

    [Fact]
    public void Replace_CollationSensitiveMatch_Declines()
    {

        var result = BuiltinRegistry.Fold(Call(
            "REPLACE", new BuiltinArgument.Text("AbcABC"), new BuiltinArgument.Text("abc"), new BuiltinArgument.Text("X")));

        Assert.Equal("non-literal-expression:replace-collation-sensitive", FailReason(result));
    }

    [Fact]
    public void Replace_EmptyPattern_Declines()
    {
        var result = BuiltinRegistry.Fold(Call(
            "REPLACE", new BuiltinArgument.Text("abc"), new BuiltinArgument.Text(string.Empty), new BuiltinArgument.Text("x")));

        Assert.Equal("non-literal-expression:replace-empty-pattern", FailReason(result));
    }

    [Fact]
    public void Replace_HoleSource_PassesThroughType()
    {
        var result = BuiltinRegistry.Fold(Call(
            "REPLACE", new BuiltinArgument.Hole(NVarChar50, HoleKind.UntypedParameter), new BuiltinArgument.Text("-"), new BuiltinArgument.Text("_")));

        Assert.Equal(NVarChar50, OkHole(result).Type);
    }

    [Fact]
    public void Replace_LiteralSourceAndPattern_HoleReplacement_SplicesHoleBetweenLiteralParts()
    {
        var result = BuiltinRegistry.Fold(Call(
            "REPLACE", new BuiltinArgument.Text("a-b-c"), new BuiltinArgument.Text("-"), new BuiltinArgument.Hole(NVarChar50, HoleKind.UntypedParameter)));

        var ok = Assert.IsType<BuiltinFoldResult.Ok>(result);

        Assert.Equal(5, ok.Pieces.Count);
        Assert.Equal("a", ((TemplatePiece.Lit)ok.Pieces[0]).Text);
        Assert.IsType<TemplatePiece.Hole>(ok.Pieces[1]);
        Assert.Equal("b", ((TemplatePiece.Lit)ok.Pieces[2]).Text);
        Assert.IsType<TemplatePiece.Hole>(ok.Pieces[3]);
        Assert.Equal("c", ((TemplatePiece.Lit)ok.Pieces[4]).Text);
    }

    [Fact]
    public void Replace_PatternAndReplacementBothHoles_Declines()
    {
        var result = BuiltinRegistry.Fold(Call(
            "REPLACE", new BuiltinArgument.Text("abc"), new BuiltinArgument.Hole(NVarChar50, HoleKind.UntypedParameter), new BuiltinArgument.Hole(NVarChar50, HoleKind.UntypedParameter)));

        Assert.Equal("symbolic-value-in-function-argument", FailReason(result));
    }

    [Fact]
    public void Replicate_AllLiteral_RepeatsInputCountTimes()
    {

        var result = BuiltinRegistry.Fold(Call("REPLICATE", new BuiltinArgument.Text("ab"), new BuiltinArgument.Number(3)));

        Assert.Equal("ababab", OkText(result));
    }

    [Fact]
    public void Replicate_ZeroCount_ProducesEmptyString()
    {

        var result = BuiltinRegistry.Fold(Call("REPLICATE", new BuiltinArgument.Text("ab"), new BuiltinArgument.Number(0)));

        Assert.Equal(string.Empty, OkText(result));
    }

    [Fact]
    public void Replicate_NegativeCount_Declines()
    {

        var result = BuiltinRegistry.Fold(Call("REPLICATE", new BuiltinArgument.Text("ab"), new BuiltinArgument.Number(-1)));

        Assert.Equal("non-literal-expression:replicate-negative-count", FailReason(result));
    }

    [Fact]
    public void Replicate_HoleSource_PassesThroughType()
    {
        var result = BuiltinRegistry.Fold(Call("REPLICATE", new BuiltinArgument.Hole(NVarChar50, HoleKind.UntypedParameter), new BuiltinArgument.Number(4)));

        Assert.Equal(NVarChar50, OkHole(result).Type);
    }

    [Fact]
    public void Replicate_UnresolvedCount_DeclinesWithCountsOwnReason()
    {
        var result = BuiltinRegistry.Fold(Call(
            "REPLICATE", new BuiltinArgument.Text("ab"), new BuiltinArgument.Unresolved("variable-not-in-scope", default)));

        Assert.Equal("variable-not-in-scope", FailReason(result));
    }

    [Fact]
    public void Reverse_AllLiteral_ReversesCharacterOrder()
    {

        var result = BuiltinRegistry.Fold(Call("REVERSE", new BuiltinArgument.Text("abc")));

        Assert.Equal("cba", OkText(result));
    }

    [Fact]
    public void Reverse_EmptyString_StaysEmpty()
    {
        var result = BuiltinRegistry.Fold(Call("REVERSE", new BuiltinArgument.Text(string.Empty)));

        Assert.Equal(string.Empty, OkText(result));
    }

    [Fact]
    public void Reverse_HoleSource_PassesThroughType()
    {
        var result = BuiltinRegistry.Fold(Call("REVERSE", new BuiltinArgument.Hole(NVarChar50, HoleKind.UntypedParameter)));

        Assert.Equal(NVarChar50, OkHole(result).Type);
    }

    [Fact]
    public void QuoteName_DefaultDelimiter_WrapsInBrackets()
    {
        var result = BuiltinRegistry.Fold(Call("QUOTENAME", new BuiltinArgument.Text("Users")));

        Assert.Equal("[Users]", OkText(result));
    }

    [Fact]
    public void QuoteName_InputOver128Characters_DeclinesNullResult()
    {
        var result = BuiltinRegistry.Fold(Call("QUOTENAME", new BuiltinArgument.Text(new string('a', 129))));

        Assert.Equal("non-literal-expression:quotename-null-result", FailReason(result));
    }

    [Fact]
    public void QuoteName_HoleArgument_ProducesNVarChar258Hole()
    {
        var result = BuiltinRegistry.Fold(Call("QUOTENAME", new BuiltinArgument.Hole(NVarChar50, HoleKind.UntypedParameter)));

        var hole = OkHole(result);
        Assert.Equal(SqlTypeCategory.NVarChar, hole.Type.Category);
        Assert.Equal(258, hole.Type.Length);
    }

    [Theory]
    [InlineData("(", "(abc)")]
    [InlineData("<", "<abc>")]
    [InlineData("{", "{abc}")]
    [InlineData("", "[abc]")]
    public void QuoteName_NonBracketDelimiters_WrapsCorrectly(string delimiter, string expected)
    {
        var result = BuiltinRegistry.Fold(Call("QUOTENAME", new BuiltinArgument.Text("abc"), new BuiltinArgument.Text(delimiter)));

        Assert.Equal(expected, OkText(result));
    }

    [Theory]
    [InlineData("(", "a)b", "(a))b)")]
    [InlineData("<", "a>b", "<a>>b>")]
    [InlineData("{", "a}b", "{a}}b}")]
    public void QuoteName_NonBracketDelimiters_DoublesEmbeddedClosingCharacter(string delimiter, string input, string expected)
    {
        var result = BuiltinRegistry.Fold(Call("QUOTENAME", new BuiltinArgument.Text(input), new BuiltinArgument.Text(delimiter)));

        Assert.Equal(expected, OkText(result));
    }

    [Theory]
    [InlineData("CHAR", 65, "A")]
    [InlineData("CHAR", 0, "\0")]
    [InlineData("NCHAR", 65, "A")]
    public void CharOrNChar_InRangeCodePoint_ProducesCharacter(string function, int codePoint, string expected)
    {
        var result = BuiltinRegistry.Fold(Call(function, new BuiltinArgument.Number(codePoint)));

        Assert.Equal(expected, OkText(result));
    }

    [Theory]
    [InlineData("CHAR", 256)]
    [InlineData("CHAR", -1)]
    [InlineData("NCHAR", 65536)]
    public void CharOrNChar_OutOfRangeCodePoint_Declines(string function, int codePoint)
    {
        var result = BuiltinRegistry.Fold(Call(function, new BuiltinArgument.Number(codePoint)));

        Assert.Equal("non-literal-expression:char-out-of-range", FailReason(result));
    }

    [Fact]
    public void Str_HoleArgument_DefaultLength_ProducesChar10Hole()
    {
        var result = BuiltinRegistry.Fold(Call("STR", new BuiltinArgument.Hole(new SqlType(SqlTypeCategory.Float), HoleKind.UntypedParameter)));

        var hole = OkHole(result);
        Assert.Equal(SqlTypeCategory.Char, hole.Type.Category);
        Assert.Equal(10, hole.Type.Length);
    }

    [Fact]
    public void Str_HoleArgumentWithExplicitLength_ProducesCharOfThatLength()
    {
        var result = BuiltinRegistry.Fold(Call(
            "STR", new BuiltinArgument.Hole(new SqlType(SqlTypeCategory.Float), HoleKind.UntypedParameter), new BuiltinArgument.Number(20)));

        Assert.Equal(20, OkHole(result).Type.Length);
    }

    [Fact]
    public void Str_ConcreteFloatExpr_StillDeclines()
    {

        var result = BuiltinRegistry.Fold(Call("STR", new BuiltinArgument.Text("3.14")));

        Assert.Equal("non-literal-expression:function-call", FailReason(result));
    }

    [Theory]
    [InlineData("NEWID", SqlTypeCategory.UniqueIdentifier)]
    [InlineData("NEWSEQUENTIALID", SqlTypeCategory.UniqueIdentifier)]
    [InlineData("GETDATE", SqlTypeCategory.DateTime)]
    [InlineData("GETUTCDATE", SqlTypeCategory.DateTime)]
    [InlineData("SYSDATETIME", SqlTypeCategory.DateTime2)]
    [InlineData("SYSUTCDATETIME", SqlTypeCategory.DateTime2)]
    [InlineData("SYSDATETIMEOFFSET", SqlTypeCategory.DateTimeOffset)]
    [InlineData("RAND", SqlTypeCategory.Float)]
    [InlineData("CHECKSUM", SqlTypeCategory.Int)]
    [InlineData("BINARY_CHECKSUM", SqlTypeCategory.Int)]
    public void NonDeterministicTyped_NoArguments_ProducesTypedHole(string function, SqlTypeCategory expectedCategory)
    {
        var result = BuiltinRegistry.Fold(Call(function));

        var hole = OkHole(result);
        Assert.Equal(expectedCategory, hole.Type.Category);
        Assert.Equal(HoleKind.NonDeterministicTyped, hole.Kind);
    }

    [Fact]
    public void ServerProperty_ProducesEnvironmentDependentTypedHole()
    {
        var withArg = OkHole(BuiltinRegistry.Fold(Call("SERVERPROPERTY", new BuiltinArgument.Text("ServerName"))));
        var withoutArg = OkHole(BuiltinRegistry.Fold(Call("SERVERPROPERTY")));

        Assert.Equal(SqlTypeCategory.SqlVariant, withArg.Type.Category);
        Assert.Equal(HoleKind.EnvironmentDependent, withArg.Kind);
        Assert.Equal(SqlTypeCategory.SqlVariant, withoutArg.Type.Category);
        Assert.Equal(HoleKind.EnvironmentDependent, withoutArg.Kind);
    }

    [Theory]
    [InlineData("DB_NAME", 128)]
    [InlineData("USER_NAME", 128)]
    [InlineData("SUSER_SNAME", 128)]
    [InlineData("SUSER_NAME", 128)]
    [InlineData("APP_NAME", 128)]
    [InlineData("HOST_NAME", 128)]
    [InlineData("SCHEMA_NAME", 128)]
    [InlineData("ORIGINAL_LOGIN", 4000)]
    public void EnvironmentNameBuiltin_NoArguments_ProducesEnvironmentDependentTypedHole(string function, int expectedLength)
    {
        var result = BuiltinRegistry.Fold(Call(function));

        var hole = OkHole(result);
        Assert.Equal(SqlTypeCategory.NVarChar, hole.Type.Category);
        Assert.Equal(expectedLength, hole.Type.Length);
        Assert.Equal(HoleKind.EnvironmentDependent, hole.Kind);
    }

    [Fact]
    public void UnknownFunction_DeclinesGenericNonLiteralFunctionCall()
    {
        var result = BuiltinRegistry.Fold(Call("SOME_UNMODELED_FUNCTION", new BuiltinArgument.Text("x")));

        Assert.Equal("non-literal-expression:function-call", FailReason(result));
    }

    [Fact]
    public void UnresolvedArgument_DeclinesWithItsOwnReasonBeforeConsultingAnySpec()
    {
        var result = BuiltinRegistry.Fold(Call(
            "UPPER", new BuiltinArgument.Unresolved("variable-not-in-scope", Site)));

        Assert.Equal("variable-not-in-scope", FailReason(result));
    }

    [Fact]
    public void FoldCastOrConvert_VarCharTarget_TruncatesOverLengthLiteral()
    {
        var target = new SqlType(SqlTypeCategory.VarChar, Length: 3);

        var result = BuiltinRegistry.FoldCastOrConvert(target, new BuiltinArgument.Text("abcdef"), Site);

        Assert.Equal("abc", OkText(result));
    }

    [Fact]
    public void FoldCastOrConvert_CharTarget_ConcreteSource_ShorterThanLength_BlankPadsToTarget()
    {

        var target = new SqlType(SqlTypeCategory.Char, Length: 5);

        var result = BuiltinRegistry.FoldCastOrConvert(target, new BuiltinArgument.Text("ab"), Site);

        Assert.Equal("ab   ", OkText(result));
    }

    [Fact]
    public void FoldCastOrConvert_CharTarget_ConcreteSource_LongerThanLength_Truncates()
    {

        var target = new SqlType(SqlTypeCategory.Char, Length: 5);

        var result = BuiltinRegistry.FoldCastOrConvert(target, new BuiltinArgument.Text("abcdef"), Site);

        Assert.Equal("abcde", OkText(result));
    }

    [Fact]
    public void FoldCastOrConvert_NCharTarget_ConcreteSource_BlankPadsToTarget()
    {
        var target = new SqlType(SqlTypeCategory.NChar, Length: 5);

        var result = BuiltinRegistry.FoldCastOrConvert(target, new BuiltinArgument.Text("abc"), Site);

        Assert.Equal("abc  ", OkText(result));
    }

    [Fact]
    public void FoldCastOrConvert_CharTarget_ConcreteSource_NoExplicitLength_DeclinesRatherThanGuessingDefault()
    {

        var target = new SqlType(SqlTypeCategory.Char);

        var result = BuiltinRegistry.FoldCastOrConvert(target, new BuiltinArgument.Text("ab"), Site);

        Assert.Equal("non-literal-expression:cast-target-not-pinned", FailReason(result));
    }

    [Fact]
    public void FoldCastOrConvert_CharTarget_HoleSource_TransfersTypeAnyway()
    {
        var target = new SqlType(SqlTypeCategory.Char, Length: 10);

        var result = BuiltinRegistry.FoldCastOrConvert(target, new BuiltinArgument.Hole(new SqlType(SqlTypeCategory.UniqueIdentifier), HoleKind.NonDeterministicTyped), Site);

        var hole = OkHole(result);
        Assert.Equal(target, hole.Type);
        Assert.Equal(HoleKind.NonDeterministicTyped, hole.Kind);
    }

    [Fact]
    public void FoldCastOrConvert_VarCharTarget_UnresolvedSource_StillTransfersTargetTypeAnyway()
    {

        var target = new SqlType(SqlTypeCategory.VarChar, Length: 200);

        var result = BuiltinRegistry.FoldCastOrConvert(target, new BuiltinArgument.Unresolved("symbolic-value-in-function-argument", Site), Site);

        var hole = OkHole(result);
        Assert.Equal(target, hole.Type);
        Assert.Equal(HoleKind.ArgumentIndependentReturnType, hole.Kind);
    }

    [Fact]
    public void FoldCastOrConvert_NonStringTarget_DeclinesCastTargetNotPinned()
    {
        var target = new SqlType(SqlTypeCategory.Int);

        var result = BuiltinRegistry.FoldCastOrConvert(target, new BuiltinArgument.Text("42"), Site);

        Assert.Equal("non-literal-expression:cast-target-not-pinned", FailReason(result));
    }
}
