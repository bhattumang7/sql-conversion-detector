using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Catalog;

public sealed class CollationTests
{
    [Theory]
    [InlineData("SQL_Latin1_General_CP1_CI_AS")]
    [InlineData("sql_latin1_general_cp1_ci_as")]
    public void IsSqlFamily_SqlPrefixedCollation_ReturnsTrue(string name)
    {
        var collation = new Collation(name);

        Assert.True(collation.IsSqlFamily);
        Assert.False(collation.IsWindowsFamily);
    }

    [Fact]
    public void IsSqlFamily_WindowsCollation_ReturnsFalse()
    {
        var collation = new Collation("Latin1_General_CI_AS");

        Assert.False(collation.IsSqlFamily);
        Assert.True(collation.IsWindowsFamily);
    }

    [Theory]
    [InlineData("Latin1_General_CS_AS")]
    [InlineData("SQL_Latin1_General_CP1_CS_AS")]
    [InlineData("Latin1_General_BIN")]
    [InlineData("Latin1_General_100_BIN2")]
    [InlineData("Latin1_General_100_BIN2_UTF8")]
    [InlineData("Japanese_XJIS_100_BIN_UTF8")]
    public void IsCaseSensitive_CsOrBinaryCollation_ReturnsTrue(string name)
    {
        Assert.True(new Collation(name).IsCaseSensitive);
    }

    [Theory]
    [InlineData("Latin1_General_CI_AS")]
    [InlineData("SQL_Latin1_General_CP1_CI_AS")]
    public void IsCaseSensitive_CiCollation_ReturnsFalse(string name)
    {
        Assert.False(new Collation(name).IsCaseSensitive);
    }

    [Theory]
    [InlineData("Latin1_General_100_CS_AI")]
    [InlineData("Latin1_General_CI_AI")]
    public void IsAccentInsensitive_AiCollation_ReturnsTrue(string name)
    {
        Assert.True(new Collation(name).IsAccentInsensitive);
    }

    [Theory]
    [InlineData("Latin1_General_CS_AS")]
    [InlineData("SQL_Latin1_General_CP1_CS_AS")]
    [InlineData("Latin1_General_100_BIN2")]
    public void IsAccentInsensitive_AsOrBinaryCollation_ReturnsFalse(string name)
    {
        Assert.False(new Collation(name).IsAccentInsensitive);
    }

    [Theory]
    [InlineData("Latin1_General_CS_AS")]
    [InlineData("SQL_Latin1_General_CP1_CS_AS")]
    [InlineData("Latin1_General_100_BIN2")]
    public void GuaranteesDistinctLiteralsAreUnequal_CaseSensitiveAndAccentSensitive_ReturnsTrue(string name)
    {
        Assert.True(new Collation(name).GuaranteesDistinctLiteralsAreUnequal);
    }

    [Theory]
    [InlineData("Latin1_General_100_CS_AI")]
    [InlineData("Latin1_General_CI_AS")]
    [InlineData("SQL_Latin1_General_CP1_CI_AS")]
    public void GuaranteesDistinctLiteralsAreUnequal_CaseInsensitiveOrAccentInsensitive_ReturnsFalse(string name)
    {
        Assert.False(new Collation(name).GuaranteesDistinctLiteralsAreUnequal);
    }

    [Fact]
    public void CoercibilityRank_ExplicitCollateClause_OutranksColumnCollation()
    {
        var explicitCollate = new Collation("Latin1_General_BIN", CollationSource.ExplicitCollateClause);
        var column = new Collation("SQL_Latin1_General_CP1_CI_AS", CollationSource.ColumnExplicit);

        Assert.True(explicitCollate.CoercibilityRank > column.CoercibilityRank);
    }

    [Fact]
    public void CoercibilityRank_ColumnCollation_AndDatabaseDefault_AreTheSameRank()
    {
        var column = new Collation("SQL_Latin1_General_CP1_CI_AS", CollationSource.ColumnExplicit);
        var databaseDefault = new Collation("Latin1_General_CI_AS", CollationSource.DatabaseDefaultFromDdl);

        Assert.Equal(column.CoercibilityRank, databaseDefault.CoercibilityRank);
    }

    [Fact]
    public void CoercibilityRank_TwoColumnCollations_AreTheSameRank()
    {
        var a = new Collation("SQL_Latin1_General_CP1_CI_AS", CollationSource.ColumnExplicit);
        var b = new Collation("Latin1_General_CI_AS", CollationSource.ColumnExplicit);

        Assert.Equal(a.CoercibilityRank, b.CoercibilityRank);
    }
}
