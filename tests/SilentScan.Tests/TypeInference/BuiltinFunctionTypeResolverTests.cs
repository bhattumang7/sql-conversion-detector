using SilentScan.Core.Catalog;
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
}
