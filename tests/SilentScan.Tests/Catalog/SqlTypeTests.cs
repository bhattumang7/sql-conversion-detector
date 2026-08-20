using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Catalog;

public sealed class SqlTypeTests
{
    [Theory]
    [InlineData(SqlTypeCategory.VarChar, true, false)]
    [InlineData(SqlTypeCategory.Char, true, false)]
    [InlineData(SqlTypeCategory.NVarChar, true, true)]
    [InlineData(SqlTypeCategory.NChar, true, true)]
    [InlineData(SqlTypeCategory.Int, false, false)]
    public void StringFamilyFlags_MatchCategory(SqlTypeCategory category, bool isString, bool isUnicode)
    {
        var type = new SqlType(category);

        Assert.Equal(isString, type.IsStringFamily);
        Assert.Equal(isUnicode, type.IsUnicodeString);
    }

    [Fact]
    public void ToString_MaxLength_RendersMax()
    {
        var type = new SqlType(SqlTypeCategory.NVarChar, IsMax: true);

        Assert.Equal("NVarChar(max)", type.ToString());
    }

    [Fact]
    public void ToString_FixedLength_RendersLength()
    {
        var type = new SqlType(SqlTypeCategory.VarChar, Length: 20);

        Assert.Equal("VarChar(20)", type.ToString());
    }

    [Fact]
    public void ToString_PrecisionAndScale_RendersBoth()
    {
        var type = new SqlType(SqlTypeCategory.Decimal, Precision: 18, Scale: 2);

        Assert.Equal("Decimal(18,2)", type.ToString());
    }

    [Fact]
    public void ToString_PrecisionOnly_RendersPrecision()
    {
        var type = new SqlType(SqlTypeCategory.Float, Precision: 53);

        Assert.Equal("Float(53)", type.ToString());
    }

    [Fact]
    public void ToString_NoFacets_RendersBareCategory()
    {
        var type = new SqlType(SqlTypeCategory.Int);

        Assert.Equal("Int", type.ToString());
    }

    [Fact]
    public void ToString_WithCollation_AppendsCollateClause()
    {
        var type = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"));

        Assert.Equal("VarChar(20) COLLATE SQL_Latin1_General_CP1_CI_AS", type.ToString());
    }
}
