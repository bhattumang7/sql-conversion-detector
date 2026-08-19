using SilentScan.Core.Catalog;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Rules;

/// <summary>
/// The gap this fix closes: LEFT/RIGHT/SUBSTRING/STUFF/REPLACE genuinely change their result's
/// own declared length (unlike UPPER/LOWER/LTRIM/RTRIM/REVERSE, whose declared length is
/// unchanged), but this class used to pass through the SOURCE argument's own length unmodified -
/// <c>LEFT(@p100, 3)</c> was typed <c>varchar(100)</c>, when the real result is <c>varchar(3)</c>,
/// fabricating an Oversized-parameter finding where the truth is under-length (or the reverse).
/// </summary>
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
        // These preserve their declared length exactly (only the runtime content, never the
        // declared max length, changes) - oracle-verified per this class's own doc comment.
        Assert.False(BuiltinFunctionTypeResolver.ResultLengthDiffersFromArgument(functionName));
    }

    [Fact]
    public void ClearLengthIfUnknown_NullsLengthAndMarksItUnknown()
    {
        var sourceType = new SqlType(SqlTypeCategory.VarChar, Length: 100, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"));

        var result = BuiltinFunctionTypeResolver.ClearLengthIfUnknown(sourceType);

        Assert.Null(result.Length);
        Assert.False(result.LengthKnown);
        // The category and collation are still real, known facts - only Length is cleared.
        Assert.Equal(SqlTypeCategory.VarChar, result.Category);
        Assert.NotNull(result.Collation);
    }
}
