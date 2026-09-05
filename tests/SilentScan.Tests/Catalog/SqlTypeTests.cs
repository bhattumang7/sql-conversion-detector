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

    [Theory]
    [InlineData(SqlTypeCategory.Text)]
    [InlineData(SqlTypeCategory.NText)]
    [InlineData(SqlTypeCategory.Image)]
    public void IsLegacyLob_LegacyLobCategories_ReturnsTrue(SqlTypeCategory category)
    {
        Assert.True(new SqlType(category).IsLegacyLob);
    }

    [Fact]
    public void IsLegacyLob_ModernLobCategory_ReturnsFalse()
    {
        Assert.False(new SqlType(SqlTypeCategory.VarChar, IsMax: true).IsLegacyLob);
    }

    [Fact]
    public void IsLegalLegacyLobConversionTarget_UnknownCollation_IsLegal()
    {
        var type = new SqlType(SqlTypeCategory.Image);

        Assert.True(type.IsLegalLegacyLobConversionTarget);
    }

    [Fact]
    public void IsLegalLegacyLobConversionTarget_LegacyLobWithOrdinaryCollation_IsLegal()
    {
        var type = new SqlType(SqlTypeCategory.NText, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"));

        Assert.True(type.IsLegalLegacyLobConversionTarget);
    }

    [Fact]
    public void IsLegalLegacyLobConversionTarget_LegacyLobWithUtf8Collation_IsIllegal()
    {
        var type = new SqlType(SqlTypeCategory.Text, Collation: new Collation("Latin1_General_100_CI_AS_SC_UTF8"));

        Assert.False(type.IsLegalLegacyLobConversionTarget);
    }

    [Fact]
    public void IsLegalLegacyLobConversionTarget_LegacyLobWithSupplementaryCharacterAwareCollation_IsIllegal()
    {
        var type = new SqlType(SqlTypeCategory.NText, Collation: new Collation("Latin1_General_100_CI_AS_SC"));

        Assert.False(type.IsLegalLegacyLobConversionTarget);
    }

    [Fact]
    public void IsLegalLegacyLobConversionTarget_ModernLobWithUtf8Collation_IsStillLegal()
    {
        var type = new SqlType(SqlTypeCategory.VarChar, IsMax: true, Collation: new Collation("Latin1_General_100_CI_AS_SC_UTF8"));

        Assert.True(type.IsLegalLegacyLobConversionTarget);
    }

    [Fact]
    public void NeedsConversionFrom_DifferentCategory_ReturnsTrue()
    {
        var target = new SqlType(SqlTypeCategory.Int);
        var source = new SqlType(SqlTypeCategory.VarChar, Length: 10);

        Assert.True(target.NeedsConversionFrom(source));
    }

    [Fact]
    public void NeedsConversionFrom_SameNonStringCategory_ReturnsFalse()
    {
        var target = new SqlType(SqlTypeCategory.Int);
        var source = new SqlType(SqlTypeCategory.Int);

        Assert.False(target.NeedsConversionFrom(source));
    }

    [Fact]
    public void NeedsConversionFrom_SameStringCategorySameCollation_ReturnsFalse()
    {
        var collation = new Collation("SQL_Latin1_General_CP1_CI_AS");
        var target = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: collation);
        var source = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: collation);

        Assert.False(target.NeedsConversionFrom(source));
    }

    [Fact]
    public void NeedsConversionFrom_SameStringCategoryDifferentCollation_ReturnsTrue()
    {
        var target = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"));
        var source = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: new Collation("Latin1_General_BIN"));

        Assert.True(target.NeedsConversionFrom(source));
    }

    [Fact]
    public void NeedsConversionFrom_SameStringCategoryUnknownCollationOnOneSide_ReturnsFalse()
    {
        var target = new SqlType(SqlTypeCategory.VarChar, Length: 10);
        var source = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: new Collation("Latin1_General_BIN"));

        Assert.False(target.NeedsConversionFrom(source));
    }
}
