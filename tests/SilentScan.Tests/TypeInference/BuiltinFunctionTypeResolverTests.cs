using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.TypeInference;

public sealed class BuiltinFunctionTypeResolverTests
{
    [Theory]
    [InlineData("LEFT")]
    [InlineData("RIGHT")]
    [InlineData("SUBSTRING")]
    [InlineData("STUFF")]
    [InlineData("REPLACE")]
    public void ResultLengthDiffersFromArgument_TrueForLengthChangingFunctions(string functionName)
    {
        Assert.True(BuiltinFunctionTypeResolver.ResultLengthDiffersFromArgument(functionName));
    }

    [Theory]
    [InlineData("UPPER")]
    [InlineData("LOWER")]
    [InlineData("LTRIM")]
    [InlineData("RTRIM")]
    [InlineData("REVERSE")]
    public void ResultLengthDiffersFromArgument_FalseForDeclaredLengthPreservingFunctions(string functionName)
    {

        Assert.False(BuiltinFunctionTypeResolver.ResultLengthDiffersFromArgument(functionName));
    }

    [Fact]
    public void ClearLengthIfUnknown_NullsLengthAndMarksItUnknown()
    {
        var sourceType = new SqlType(SqlTypeCategory.VarChar, Length: 100, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"));

        var result = BuiltinFunctionTypeResolver.ClearLengthIfUnknown(sourceType);

        Assert.Null(result.Length);
        Assert.False(result.LengthKnown);

        Assert.Equal(SqlTypeCategory.VarChar, result.Category);
        Assert.NotNull(result.Collation);
    }

    [Theory]
    [InlineData("UPPER")]
    [InlineData("LOWER")]
    [InlineData("LTRIM")]
    [InlineData("RTRIM")]
    [InlineData("REVERSE")]
    [InlineData("REPLACE")]
    [InlineData("LEFT")]
    [InlineData("RIGHT")]
    [InlineData("SUBSTRING")]
    [InlineData("STUFF")]
    public void DemotesFixedWidthArgumentCategory_TrueForEveryStringTransformBuiltin(string functionName)
    {
        Assert.True(BuiltinFunctionTypeResolver.DemotesFixedWidthArgumentCategory(functionName));
    }

    [Theory]
    [InlineData("ISNULL")]
    [InlineData("MIN")]
    [InlineData("MAX")]
    public void DemotesFixedWidthArgumentCategory_FalseForNonTransformFunctions(string functionName)
    {

        Assert.False(BuiltinFunctionTypeResolver.DemotesFixedWidthArgumentCategory(functionName));
    }

    [Theory]
    [InlineData(SqlTypeCategory.Char, SqlTypeCategory.VarChar)]
    [InlineData(SqlTypeCategory.NChar, SqlTypeCategory.NVarChar)]
    [InlineData(SqlTypeCategory.Binary, SqlTypeCategory.VarBinary)]
    public void DemoteFixedWidthCategory_DemotesFixedWidthCategories(SqlTypeCategory source, SqlTypeCategory expected)
    {
        var result = BuiltinFunctionTypeResolver.DemoteFixedWidthCategory(new SqlType(source, Length: 10));

        Assert.Equal(expected, result.Category);
        Assert.Equal(10, result.Length);
    }

    [Fact]
    public void DemoteFixedWidthCategory_AlreadyVariableWidth_PassesThroughUnchanged()
    {
        var source = new SqlType(SqlTypeCategory.VarChar, Length: 10);

        Assert.Equal(source, BuiltinFunctionTypeResolver.DemoteFixedWidthCategory(source));
    }

    [Fact]
    public void ResolveStringAggResult_NonUnicodeValue_CapsAt8000()
    {
        var result = BuiltinFunctionTypeResolver.ResolveStringAggResult(new SqlType(SqlTypeCategory.VarChar, Length: 10));

        Assert.Equal(SqlTypeCategory.VarChar, result!.Category);
        Assert.Equal(8000, result.Length);
        Assert.False(result.IsMax);
    }

    [Fact]
    public void ResolveStringAggResult_UnicodeValue_CapsAt4000()
    {
        var result = BuiltinFunctionTypeResolver.ResolveStringAggResult(new SqlType(SqlTypeCategory.NVarChar, Length: 10));

        Assert.Equal(SqlTypeCategory.NVarChar, result!.Category);
        Assert.Equal(4000, result.Length);
        Assert.False(result.IsMax);
    }

    [Fact]
    public void ResolveStringAggResult_MaxTypedValue_IsNotCapped()
    {
        var result = BuiltinFunctionTypeResolver.ResolveStringAggResult(new SqlType(SqlTypeCategory.VarChar, IsMax: true));

        Assert.True(result!.IsMax);
    }

    [Fact]
    public void ResolveStringAggResult_FixedWidthValueCategory_DemotesToVariableWidth()
    {
        var result = BuiltinFunctionTypeResolver.ResolveStringAggResult(new SqlType(SqlTypeCategory.Char, Length: 10));

        Assert.Equal(SqlTypeCategory.VarChar, result!.Category);
        Assert.Equal(8000, result.Length);
    }

    [Fact]
    public void ResolveStringAggResult_NonStringValue_ReturnsNull()
    {
        var result = BuiltinFunctionTypeResolver.ResolveStringAggResult(new SqlType(SqlTypeCategory.Int));

        Assert.Null(result);
    }

    [Theory]
    [InlineData("UNICODE", SqlTypeCategory.Int)]
    [InlineData("CHAR", SqlTypeCategory.Char)]
    [InlineData("NCHAR", SqlTypeCategory.NChar)]
    [InlineData("SPACE", SqlTypeCategory.VarChar)]
    [InlineData("QUOTENAME", SqlTypeCategory.NVarChar)]
    [InlineData("SOUNDEX", SqlTypeCategory.VarChar)]
    [InlineData("DIFFERENCE", SqlTypeCategory.Int)]
    [InlineData("ISJSON", SqlTypeCategory.Int)]
    public void ResolveFixedReturnType_OracleVerified_NewlyCoveredBuiltins_ResolveToDocumentedCategory(string functionName, SqlTypeCategory expectedCategory)
    {
        var result = BuiltinFunctionTypeResolver.ResolveFixedReturnType(functionName);

        Assert.Equal(expectedCategory, result!.Category);
    }

    [Fact]
    public void ResolveFixedReturnType_OracleVerified_Quotename_CapsAtTwoHundredFiftyEight()
    {
        var result = BuiltinFunctionTypeResolver.ResolveFixedReturnType("QUOTENAME");

        Assert.Equal(258, result!.Length);
    }

    [Fact]
    public void ResolveFixedReturnType_OracleVerified_Soundex_ReturnsLengthFiveNotFour()
    {
        var result = BuiltinFunctionTypeResolver.ResolveFixedReturnType("SOUNDEX");

        Assert.Equal(5, result!.Length);
    }

    [Theory]
    [InlineData("TRIM")]
    [InlineData("TRANSLATE")]
    public void TryGetArgumentTypeIndex_OracleVerified_NewlyCoveredBuiltins_ResolveFromFirstArgument(string functionName)
    {
        Assert.Equal(0, BuiltinFunctionTypeResolver.TryGetArgumentTypeIndex(functionName));
    }

    [Fact]
    public void DemotesFixedWidthArgumentCategory_OracleVerified_Trim_TrueLikeLtrimRtrim()
    {
        Assert.True(BuiltinFunctionTypeResolver.DemotesFixedWidthArgumentCategory("TRIM"));
    }

    [Fact]
    public void ResultLengthDiffersFromArgument_OracleVerified_Trim_FalseBecauseLengthIsPreserved()
    {
        Assert.False(BuiltinFunctionTypeResolver.ResultLengthDiffersFromArgument("TRIM"));
    }

    [Fact]
    public void ResultLengthDiffersFromArgument_OracleVerified_Translate_TrueBecauseResultCapsAtMaximumWidth()
    {
        Assert.True(BuiltinFunctionTypeResolver.ResultLengthDiffersFromArgument("TRANSLATE"));
    }

    [Fact]
    public void DemotesFixedWidthArgumentCategory_OracleVerified_Translate_True()
    {
        Assert.True(BuiltinFunctionTypeResolver.DemotesFixedWidthArgumentCategory("TRANSLATE"));
    }
}
