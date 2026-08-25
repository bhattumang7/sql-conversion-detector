using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.TypeInference;

public sealed class ExpressionTypeInferencerTests
{
    private static readonly SqlType IntType = new(SqlTypeCategory.Int);
    private static readonly SqlType DecimalType = new(SqlTypeCategory.Decimal, Precision: 9, Scale: 2);
    private static readonly SqlType VarCharType = new(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"));
    private static readonly SqlType NVarCharType = new(SqlTypeCategory.NVarChar, Length: 20);

    private static ScalarExpression ParseExpression(string expressionSql)
    {
        var parser = new TSql160Parser(true);
        using var reader = new StringReader($"SELECT {expressionSql};");
        var fragment = parser.Parse(reader, out var errors);
        Assert.Empty(errors);

        var script = (TSqlScript)fragment;
        var select = (SelectStatement)script.Batches[0].Statements[0];
        var spec = (QuerySpecification)select.QueryExpression;
        return ((SelectScalarExpression)spec.SelectElements[0]).Expression;
    }

    private static SqlType? StubLeaf(ScalarExpression expression, IReadOnlyDictionary<string, SqlType?> typesByName) =>
        expression is ColumnReferenceExpression { MultiPartIdentifier.Identifiers: [.., { } last] }
            ? typesByName.GetValueOrDefault(last.Value)
            : null;

    private static SqlType? Resolve(string expressionSql, IReadOnlyDictionary<string, SqlType?> typesByName) =>
        ExpressionTypeInferencer.Resolve(ParseExpression(expressionSql), e => StubLeaf(e, typesByName), typeAliases: null);

    [Fact]
    public void Resolve_Arithmetic_CombinesByPrecedence()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType, ["DecCol"] = DecimalType };

        var result = Resolve("IntCol * DecCol", typesByName);

        Assert.Equal(SqlTypeCategory.Decimal, result!.Category);
    }

    [Fact]
    public void Resolve_Parenthesis_UnwrapsToInnerType()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType };

        Assert.Equal(SqlTypeCategory.Int, Resolve("(IntCol)", typesByName)!.Category);
    }

    [Fact]
    public void Resolve_Unary_UnwrapsToInnerType()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType };

        Assert.Equal(SqlTypeCategory.Int, Resolve("-IntCol", typesByName)!.Category);
    }

    [Fact]
    public void Resolve_CastCall_ResolvesTargetType()
    {
        Assert.Equal(SqlTypeCategory.NVarChar, Resolve("CAST(1 AS NVARCHAR(10))", new Dictionary<string, SqlType?>())!.Category);
    }

    [Fact]
    public void Resolve_SearchedCase_OracleVerified_MergesBranchesByPrecedence()
    {

        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType, ["DecCol"] = DecimalType };

        var result = Resolve("CASE WHEN 1 = 1 THEN IntCol ELSE DecCol END", typesByName);

        Assert.Equal(SqlTypeCategory.Decimal, result!.Category);
    }

    [Fact]
    public void Resolve_SimpleCase_OracleVerified_MergesBranchesByPrecedence()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType, ["DecCol"] = DecimalType };

        var result = Resolve("CASE IntCol WHEN 1 THEN IntCol ELSE DecCol END", typesByName);

        Assert.Equal(SqlTypeCategory.Decimal, result!.Category);
    }

    [Fact]
    public void Resolve_Coalesce_OracleVerified_MergesAllBranchesByPrecedence()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["VarcharCol"] = VarCharType, ["NVarcharCol"] = NVarCharType };

        var result = Resolve("COALESCE(VarcharCol, NVarcharCol)", typesByName);

        Assert.Equal(SqlTypeCategory.NVarChar, result!.Category);
    }

    [Fact]
    public void Resolve_Coalesce_OneUnresolvableBranch_NullsWholeResult()
    {

        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType };

        Assert.Null(Resolve("COALESCE(IntCol, UnknownCol)", typesByName));
    }

    [Fact]
    public void Resolve_IIf_OracleVerified_BehavesLikeCase()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType, ["DecCol"] = DecimalType };

        var result = Resolve("IIF(1 = 1, IntCol, DecCol)", typesByName);

        Assert.Equal(SqlTypeCategory.Decimal, result!.Category);
    }

    [Fact]
    public void Resolve_NullIf_OracleVerified_AlwaysReturnsFirstExpressionType_NotPrecedenceMerge()
    {

        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType, ["DecCol"] = DecimalType };

        var result = Resolve("NULLIF(IntCol, DecCol)", typesByName);

        Assert.Equal(SqlTypeCategory.Int, result!.Category);
    }

    [Fact]
    public void Resolve_NullIf_ReversedOperandOrder_StillReturnsFirstExpressionType()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType, ["DecCol"] = DecimalType };

        var result = Resolve("NULLIF(DecCol, IntCol)", typesByName);

        Assert.Equal(SqlTypeCategory.Decimal, result!.Category);
    }

    [Fact]
    public void Resolve_SearchedCase_OracleVerified_BareNullBranchIsIgnoredNotMergeed()
    {

        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType };

        var result = Resolve("CASE WHEN 1 = 1 THEN NULL ELSE IntCol END", typesByName);

        Assert.Equal(SqlTypeCategory.Int, result!.Category);
    }

    [Fact]
    public void Resolve_Coalesce_BareNullArgument_IsIgnoredNotMerged()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType };

        var result = Resolve("COALESCE(NULL, IntCol)", typesByName);

        Assert.Equal(SqlTypeCategory.Int, result!.Category);
    }

    [Fact]
    public void Resolve_IIf_BareNullBranch_IsIgnoredNotMerged()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType };

        var result = Resolve("IIF(1 = 1, NULL, IntCol)", typesByName);

        Assert.Equal(SqlTypeCategory.Int, result!.Category);
    }

    [Fact]
    public void Resolve_Coalesce_OneUnresolvableNonNullBranch_StillNullsWholeResult()
    {

        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType };

        Assert.Null(Resolve("COALESCE(NULL, IntCol, UnknownCol)", typesByName));
    }

    [Fact]
    public void Resolve_SearchedCase_OracleVerified_SameCategoryDifferingLength_WidensToTheLonger()
    {

        var typesByName = new Dictionary<string, SqlType?>
        {
            ["Nvarchar10Col"] = new SqlType(SqlTypeCategory.NVarChar, Length: 10),
            ["Nvarchar20Col"] = new SqlType(SqlTypeCategory.NVarChar, Length: 20),
        };

        var result = Resolve("CASE WHEN 1 = 1 THEN Nvarchar10Col ELSE Nvarchar20Col END", typesByName);

        Assert.Equal(SqlTypeCategory.NVarChar, result!.Category);
        Assert.Equal(20, result.Length);
    }

    [Fact]
    public void Resolve_SearchedCase_OracleVerified_SameCategoryDifferingLengthReversedOrder_StillWidensToTheLonger()
    {
        var typesByName = new Dictionary<string, SqlType?>
        {
            ["Nvarchar10Col"] = new SqlType(SqlTypeCategory.NVarChar, Length: 10),
            ["Nvarchar20Col"] = new SqlType(SqlTypeCategory.NVarChar, Length: 20),
        };

        var result = Resolve("CASE WHEN 1 = 1 THEN Nvarchar20Col ELSE Nvarchar10Col END", typesByName);

        Assert.Equal(20, result!.Length);
    }

    [Fact]
    public void Resolve_SearchedCase_OracleVerified_OneBranchIsMax_ResultIsMaxRegardlessOfPosition()
    {

        var typesByName = new Dictionary<string, SqlType?>
        {
            ["Nvarchar10Col"] = new SqlType(SqlTypeCategory.NVarChar, Length: 10),
            ["NvarcharMaxCol"] = new SqlType(SqlTypeCategory.NVarChar, IsMax: true),
        };

        var thenIsMax = Resolve("CASE WHEN 1 = 1 THEN NvarcharMaxCol ELSE Nvarchar10Col END", typesByName);
        var elseIsMax = Resolve("CASE WHEN 1 = 1 THEN Nvarchar10Col ELSE NvarcharMaxCol END", typesByName);

        Assert.True(thenIsMax!.IsMax);
        Assert.True(elseIsMax!.IsMax);
    }

    [Fact]
    public void Resolve_SearchedCase_OracleVerified_SameCategorySameLength_PreservesTheLength()
    {

        var typesByName = new Dictionary<string, SqlType?>
        {
            ["A"] = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS")),
            ["B"] = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS")),
        };

        var result = Resolve("CASE WHEN 1 = 1 THEN A ELSE B END", typesByName);

        Assert.Equal(20, result!.Length);
    }

    [Fact]
    public void Resolve_StringConcatenation_OracleVerified_SumsLengthsRatherThanMax()
    {

        var typesByName = new Dictionary<string, SqlType?>
        {
            ["A"] = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS")),
            ["B"] = new SqlType(SqlTypeCategory.VarChar, Length: 15, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS")),
        };

        var result = Resolve("A + B", typesByName);

        Assert.Equal(SqlTypeCategory.VarChar, result!.Category);
        Assert.Equal(25, result.Length);
    }

    [Fact]
    public void Resolve_StringConcatenation_OracleVerified_CapsAtHardMaximumRatherThanPromotingToMax()
    {

        var typesByName = new Dictionary<string, SqlType?>
        {
            ["A"] = new SqlType(SqlTypeCategory.VarChar, Length: 5000),
            ["B"] = new SqlType(SqlTypeCategory.VarChar, Length: 5000),
        };

        var result = Resolve("A + B", typesByName);

        Assert.False(result!.IsMax);
        Assert.Equal(8000, result.Length);
    }

    [Fact]
    public void Resolve_StringConcatenation_NvarcharCapsAtFourThousandCharacters()
    {
        var typesByName = new Dictionary<string, SqlType?>
        {
            ["A"] = new SqlType(SqlTypeCategory.NVarChar, Length: 3000),
            ["B"] = new SqlType(SqlTypeCategory.NVarChar, Length: 3000),
        };

        var result = Resolve("A + B", typesByName);

        Assert.False(result!.IsMax);
        Assert.Equal(4000, result.Length);
    }

    [Fact]
    public void Resolve_StringConcatenation_EitherSideMax_ResultIsMax()
    {

        var typesByName = new Dictionary<string, SqlType?>
        {
            ["A"] = new SqlType(SqlTypeCategory.VarChar, IsMax: true),
            ["B"] = new SqlType(SqlTypeCategory.VarChar, Length: 10),
        };

        Assert.True(Resolve("A + B", typesByName)!.IsMax);
    }

    [Fact]
    public void Resolve_CrossCategoryStringMerge_LengthUnknownRatherThanImplicitlyNulled()
    {

        var typesByName = new Dictionary<string, SqlType?>
        {
            ["NvarcharCol"] = new SqlType(SqlTypeCategory.NVarChar, Length: 20),
            ["CharCol"] = new SqlType(SqlTypeCategory.Char, Length: 10),
        };

        var result = Resolve("CASE WHEN 1 = 1 THEN NvarcharCol ELSE CharCol END", typesByName);

        Assert.Equal(SqlTypeCategory.NVarChar, result!.Category);
        Assert.Null(result.Length);
        Assert.False(result.LengthKnown);
    }

    private static SqlType Decimal(int precision, int scale) => new(SqlTypeCategory.Decimal, Precision: precision, Scale: scale);

    [Theory]
    [InlineData("A + B", 6, 2)]
    [InlineData("A - B", 6, 2)]
    [InlineData("A * B", 11, 4)]
    [InlineData("A / B", 13, 8)]
    public void Resolve_Arithmetic_OracleVerified_DecimalWithDecimal_ExactPrecisionAndScale(string expressionSql, int expectedPrecision, int expectedScale)
    {
        var typesByName = new Dictionary<string, SqlType?> { ["A"] = Decimal(5, 2), ["B"] = Decimal(5, 2) };

        var result = Resolve(expressionSql, typesByName);

        Assert.Equal(SqlTypeCategory.Decimal, result!.Category);
        Assert.Equal(expectedPrecision, result.Precision);
        Assert.Equal(expectedScale, result.Scale);
    }

    [Fact]
    public void Resolve_Arithmetic_OracleVerified_IntPlusDecimal_NormalizesIntToDecimalTenZero()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["A"] = IntType, ["B"] = Decimal(5, 2) };

        var result = Resolve("A + B", typesByName);

        Assert.Equal(13, result!.Precision);
        Assert.Equal(2, result.Scale);
    }

    [Fact]
    public void Resolve_Arithmetic_OracleVerified_DecimalPlusInt_SameResultRegardlessOfOperandOrder()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["A"] = Decimal(5, 2), ["B"] = IntType };

        var result = Resolve("A + B", typesByName);

        Assert.Equal(13, result!.Precision);
        Assert.Equal(2, result.Scale);
    }

    [Fact]
    public void Resolve_Arithmetic_OracleVerified_BigIntPlusDecimal_NormalizesBigIntToDecimalNineteenZero()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["A"] = new SqlType(SqlTypeCategory.BigInt), ["B"] = Decimal(5, 2) };

        var result = Resolve("A + B", typesByName);

        Assert.Equal(22, result!.Precision);
        Assert.Equal(2, result.Scale);
    }

    [Fact]
    public void Resolve_Arithmetic_OracleVerified_TinyIntPlusDecimal_NormalizesTinyIntToDecimalThreeZero()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["A"] = new SqlType(SqlTypeCategory.TinyInt), ["B"] = Decimal(5, 2) };

        var result = Resolve("A + B", typesByName);

        Assert.Equal(6, result!.Precision);
        Assert.Equal(2, result.Scale);
    }

    [Fact]
    public void Resolve_Arithmetic_OracleVerified_SmallIntPlusDecimal_NormalizesSmallIntToDecimalFiveZero()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["A"] = new SqlType(SqlTypeCategory.SmallInt), ["B"] = Decimal(5, 2) };

        var result = Resolve("A + B", typesByName);

        Assert.Equal(8, result!.Precision);
        Assert.Equal(2, result.Scale);
    }

    [Fact]
    public void Resolve_Arithmetic_OracleVerified_MultiplyMixedPrecisionScale()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["A"] = Decimal(9, 2), ["B"] = Decimal(9, 4) };

        var result = Resolve("A * B", typesByName);

        Assert.Equal(19, result!.Precision);
        Assert.Equal(6, result.Scale);
    }

    [Fact]
    public void Resolve_Arithmetic_OracleVerified_DivideMixedPrecisionScale()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["A"] = Decimal(9, 2), ["B"] = Decimal(5, 4) };

        var result = Resolve("A / B", typesByName);

        Assert.Equal(19, result!.Precision);
        Assert.Equal(8, result.Scale);
    }

    [Fact]
    public void Resolve_Arithmetic_OracleVerified_AddExactlyAtPrecisionThirtyEight_NoCappingNeeded()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["A"] = Decimal(38, 0), ["B"] = Decimal(38, 0) };

        var result = Resolve("A + B", typesByName);

        Assert.Equal(38, result!.Precision);
        Assert.Equal(0, result.Scale);
    }

    [Fact]
    public void Resolve_Arithmetic_OracleVerified_AddOverflowsThirtyEight_ScaleUnchangedIntegralPartTruncated()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["A"] = Decimal(38, 37), ["B"] = Decimal(38, 37) };

        var result = Resolve("A + B", typesByName);

        Assert.Equal(38, result!.Precision);
        Assert.Equal(37, result.Scale);
    }

    [Fact]
    public void Resolve_Arithmetic_OracleVerified_AddOverflowsThirtyEight_ScaleCanReduceBelowMultiplyDivideFloor()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["A"] = Decimal(38, 0), ["B"] = Decimal(38, 38) };

        var result = Resolve("A + B", typesByName);

        Assert.Equal(38, result!.Precision);
        Assert.Equal(0, result.Scale);
    }

    [Fact]
    public void Resolve_Arithmetic_OracleVerified_MultiplyOverflowsThirtyEight_ScaleFloorsAtSix()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["A"] = Decimal(38, 10), ["B"] = Decimal(38, 0) };

        var result = Resolve("A * B", typesByName);

        Assert.Equal(38, result!.Precision);
        Assert.Equal(6, result.Scale);
    }

    [Fact]
    public void Resolve_Arithmetic_OracleVerified_MultiplyOverflow_PreservesIntegralDigitsWhenUnderThirtyTwo()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["A"] = Decimal(20, 10), ["B"] = Decimal(20, 10) };

        var result = Resolve("A * B", typesByName);

        Assert.Equal(38, result!.Precision);
        Assert.Equal(17, result.Scale);
    }

    [Fact]
    public void Resolve_Arithmetic_OracleVerified_DivideOverflowsThirtyEight_ScaleFloorsAtSix()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["A"] = Decimal(38, 0), ["B"] = Decimal(38, 38) };

        var result = Resolve("A / B", typesByName);

        Assert.Equal(38, result!.Precision);
        Assert.Equal(6, result.Scale);
    }

    [Fact]
    public void Resolve_Arithmetic_OracleVerified_MoneyPlusDecimal_NormalizesMoneyToDecimalNineteenFour()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["A"] = new SqlType(SqlTypeCategory.Money), ["B"] = Decimal(5, 2) };

        var result = Resolve("A + B", typesByName);

        Assert.Equal(SqlTypeCategory.Decimal, result!.Category);
        Assert.Equal(20, result.Precision);
        Assert.Equal(4, result.Scale);
    }

    [Fact]
    public void Resolve_Arithmetic_OracleVerified_MoneyPlusMoney_NoDecimalOperand_StaysMoney()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["A"] = new SqlType(SqlTypeCategory.Money), ["B"] = new SqlType(SqlTypeCategory.Money) };

        var result = Resolve("A + B", typesByName);

        Assert.Equal(SqlTypeCategory.Money, result!.Category);
    }

    [Fact]
    public void Resolve_Arithmetic_FloatOperand_UnresolvedByExactFormula_FallsBackToPrecedenceOnly()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["A"] = new SqlType(SqlTypeCategory.Float), ["B"] = Decimal(5, 2) };

        var result = Resolve("A + B", typesByName);

        Assert.Equal(SqlTypeCategory.Float, result!.Category);
    }

    [Fact]
    public void Resolve_Arithmetic_PureIntegerFamily_NeverPromotedToDecimal()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["A"] = IntType, ["B"] = new SqlType(SqlTypeCategory.BigInt) };

        var result = Resolve("A + B", typesByName);

        Assert.Equal(SqlTypeCategory.BigInt, result!.Category);
        Assert.Null(result.Precision);
    }

    [Fact]
    public void Resolve_Arithmetic_DecimalOperandMissingScale_DeclinesExactFormula_FallsBackToPrecedenceOnly()
    {
        var typesByName = new Dictionary<string, SqlType?>
        {
            ["A"] = new SqlType(SqlTypeCategory.Decimal, Precision: 9),
            ["B"] = Decimal(5, 2),
        };

        var result = Resolve("A + B", typesByName);

        Assert.Equal(SqlTypeCategory.Decimal, result!.Category);
        Assert.Equal(9, result.Precision);
        Assert.Null(result.Scale);
    }

    [Fact]
    public void Resolve_FunctionCall_FixedReturnTypeBuiltin_ResolvesToBuiltinRegistryType()
    {
        var result = Resolve("GETDATE()", new Dictionary<string, SqlType?>());

        Assert.Equal(SqlTypeCategory.DateTime, result!.Category);
    }

    [Fact]
    public void Resolve_FunctionCall_ArgumentTypeBuiltin_ResolvesArgumentExpressionRecursively()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType, ["DecCol"] = DecimalType };

        var result = Resolve("ISNULL(IntCol * DecCol, 0)", typesByName);

        Assert.Equal(SqlTypeCategory.Decimal, result!.Category);
    }

    [Fact]
    public void Resolve_FunctionCall_LengthVaryingBuiltin_ClearsLengthRatherThanKeepingArgumentLength()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["A"] = VarCharType };

        var result = Resolve("SUBSTRING(A, 1, 5)", typesByName);

        Assert.Equal(SqlTypeCategory.VarChar, result!.Category);
        Assert.False(result.LengthKnown);
    }

    [Fact]
    public void Resolve_FunctionCall_UnknownFunction_ResolvesToNullRatherThanGuessing()
    {
        var result = Resolve("dbo.SomeScalarFunction(1)", new Dictionary<string, SqlType?>());

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_LeftFunctionCall_ParsesToItsOwnScriptDomNodeType_StillResolvesArgumentType()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["A"] = VarCharType };

        var result = Resolve("LEFT(A, 5)", typesByName);

        Assert.Equal(SqlTypeCategory.VarChar, result!.Category);
        Assert.False(result.LengthKnown);
    }

    [Fact]
    public void Resolve_RightFunctionCall_ParsesToItsOwnScriptDomNodeType_StillResolvesArgumentType()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["A"] = VarCharType };

        var result = Resolve("RIGHT(A, 5)", typesByName);

        Assert.Equal(SqlTypeCategory.VarChar, result!.Category);
        Assert.False(result.LengthKnown);
    }
}
