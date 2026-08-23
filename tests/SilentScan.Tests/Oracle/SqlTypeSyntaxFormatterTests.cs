using SilentScan.Core.Catalog;
using SilentScan.Verify.Oracle;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Oracle;

public sealed class SqlTypeSyntaxFormatterTests
{
    [Fact]
    public void Format_VarCharWithLength_RendersLengthFacet()
    {
        Assert.Equal("VARCHAR(20)", SqlTypeSyntaxFormatter.Format(new SqlType(SqlTypeCategory.VarChar, Length: 20)));
    }

    [Fact]
    public void Format_NVarCharMax_RendersMaxFacet()
    {
        Assert.Equal("NVARCHAR(MAX)", SqlTypeSyntaxFormatter.Format(new SqlType(SqlTypeCategory.NVarChar, IsMax: true)));
    }

    [Fact]
    public void Format_VarCharWithMissingLength_FallsBackToPermissiveDefault()
    {
        Assert.Equal("VARCHAR(4000)", SqlTypeSyntaxFormatter.Format(new SqlType(SqlTypeCategory.VarChar)));
    }

    [Fact]
    public void Format_DecimalWithPrecisionAndScale_RendersBothFacets()
    {
        Assert.Equal("DECIMAL(10,2)", SqlTypeSyntaxFormatter.Format(new SqlType(SqlTypeCategory.Decimal, Precision: 10, Scale: 2)));
    }

    [Fact]
    public void Format_IntCategory_NoFacet()
    {
        Assert.Equal("INT", SqlTypeSyntaxFormatter.Format(new SqlType(SqlTypeCategory.Int)));
    }

    [Fact]
    public void Format_WithCollation_NeverAppendsCollateClause()
    {
        var type = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: new Collation("Latin1_General_CI_AS"));

        Assert.Equal("VARCHAR(10)", SqlTypeSyntaxFormatter.Format(type));
    }

    [Fact]
    public void FormatCollateClause_WithCollation_ReturnsCollateSuffix()
    {
        var type = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: new Collation("Latin1_General_CI_AS"));

        Assert.Equal(" COLLATE Latin1_General_CI_AS", SqlTypeSyntaxFormatter.FormatCollateClause(type));
    }

    [Fact]
    public void FormatCollateClause_NoCollation_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, SqlTypeSyntaxFormatter.FormatCollateClause(new SqlType(SqlTypeCategory.Int)));
    }

    [Fact]
    public void Format_SqlVariant_UsesUnderscoredKeyword()
    {
        Assert.Equal("SQL_VARIANT", SqlTypeSyntaxFormatter.Format(new SqlType(SqlTypeCategory.SqlVariant)));
    }

    [Fact]
    public void Format_UserDefinedType_ReturnsNull()
    {
        Assert.Null(SqlTypeSyntaxFormatter.Format(new SqlType(SqlTypeCategory.UserDefined)));
    }
}
