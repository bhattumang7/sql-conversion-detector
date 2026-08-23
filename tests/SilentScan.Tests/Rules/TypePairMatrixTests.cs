using SilentScan.Core.Catalog;
using SilentScan.Core.Rules;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Rules;

public sealed class TypePairMatrixTests
{
    [Fact]
    public void Instance_LoadsWithoutThrowing() =>
        Assert.NotNull(TypePairMatrix.Instance);

    [Fact]
    public void Instance_HasServerVersionAndProbeDateProvenance()
    {
        Assert.False(string.IsNullOrWhiteSpace(TypePairMatrix.Instance.ServerVersion));
        Assert.False(string.IsNullOrWhiteSpace(TypePairMatrix.Instance.ProbedAtUtc));
    }

    [Fact]
    public void Instance_HasAllExpectedEntries() =>

        Assert.Equal(790, TypePairMatrix.Instance.Entries.Count);

    [Fact]
    public void Instance_ProbesEveryDeclaredCollationWithTheSameEntryCount()
    {

        var entries = TypePairMatrix.Instance.Entries;
        var byCollation = entries.Where(e => e.CollationName is not null).ToLookup(e => e.CollationName);

        Assert.Equal(SilentScan.Verify.Oracle.TypeMatrixGenerator.Collations.Count, byCollation.Count);
        foreach (var collation in SilentScan.Verify.Oracle.TypeMatrixGenerator.Collations)
        {
            Assert.True(byCollation.Contains(collation), $"matrix has no entries for collation '{collation}'");

            Assert.Equal(92, byCollation[collation].Count());
        }
    }

    [Theory]
    [InlineData(SqlTypeCategory.Int, SqlTypeCategory.Real, true)]
    [InlineData(SqlTypeCategory.BigInt, SqlTypeCategory.Real, true)]
    [InlineData(SqlTypeCategory.BigInt, SqlTypeCategory.Float, true)]
    [InlineData(SqlTypeCategory.SmallMoney, SqlTypeCategory.Real, true)]
    [InlineData(SqlTypeCategory.Money, SqlTypeCategory.Float, true)]
    [InlineData(SqlTypeCategory.Decimal, SqlTypeCategory.Real, true)]
    [InlineData(SqlTypeCategory.TinyInt, SqlTypeCategory.Real, false)]
    [InlineData(SqlTypeCategory.SmallInt, SqlTypeCategory.Float, false)]
    [InlineData(SqlTypeCategory.Int, SqlTypeCategory.BigInt, false)]
    [InlineData(SqlTypeCategory.Bit, SqlTypeCategory.Int, false)]
    [InlineData(SqlTypeCategory.Bit, SqlTypeCategory.BigInt, false)]
    [InlineData(SqlTypeCategory.Date, SqlTypeCategory.DateTime, false)]
    [InlineData(SqlTypeCategory.SmallDateTime, SqlTypeCategory.DateTime2, false)]
    public void TryGetOutcome_NumericAndDateTimeFamilyPairs_MatchesOracleProbe(SqlTypeCategory column, SqlTypeCategory other, bool expectedColumnConverts)
    {
        var outcome = TypePairMatrix.Instance.TryGetOutcome(column, other);

        Assert.NotNull(outcome);
        Assert.False(outcome.CompileFailed);
        Assert.Equal(expectedColumnConverts, outcome.ColumnConverts);
    }

    [Theory]
    [InlineData(SqlTypeCategory.Time, SqlTypeCategory.Date)]
    [InlineData(SqlTypeCategory.Time, SqlTypeCategory.SmallDateTime)]
    [InlineData(SqlTypeCategory.Time, SqlTypeCategory.DateTime)]
    [InlineData(SqlTypeCategory.Time, SqlTypeCategory.DateTime2)]
    [InlineData(SqlTypeCategory.Time, SqlTypeCategory.DateTimeOffset)]
    [InlineData(SqlTypeCategory.Date, SqlTypeCategory.Time)]
    public void TryGetOutcome_TimeAgainstOtherDateTimeCategories_CompileFailed(SqlTypeCategory column, SqlTypeCategory other)
    {
        var outcome = TypePairMatrix.Instance.TryGetOutcome(column, other);

        Assert.NotNull(outcome);
        Assert.True(outcome.CompileFailed);
    }

    [Fact]
    public void TryGetOutcome_UnprobedPair_ReturnsNull() =>
        Assert.Null(TypePairMatrix.Instance.TryGetOutcome(SqlTypeCategory.Xml, SqlTypeCategory.SqlVariant));

    [Theory]
    [InlineData(SqlTypeCategory.VarChar, SqlTypeCategory.NVarChar, "SQL_Latin1_General_CP1_CI_AS", true, false)]
    [InlineData(SqlTypeCategory.VarChar, SqlTypeCategory.NVarChar, "Latin1_General_CI_AS", true, true)]
    [InlineData(SqlTypeCategory.NVarChar, SqlTypeCategory.VarChar, "SQL_Latin1_General_CP1_CI_AS", false, false)]
    public void TryGetOutcome_StringFamilyPairs_KeyedByCollation(
        SqlTypeCategory column, SqlTypeCategory other, string collation, bool expectedColumnConverts, bool expectedRangeSeek)
    {
        var outcome = TypePairMatrix.Instance.TryGetOutcome(column, other, collation);

        Assert.NotNull(outcome);
        Assert.Equal(expectedColumnConverts, outcome.ColumnConverts);
        Assert.Equal(expectedRangeSeek, outcome.DynamicRangeSeekAvailable);
    }

    [Fact]
    public void TryGetOutcome_StringPairWithoutCollationArgument_DoesNotMatchCollationSpecificEntry() =>

        Assert.Null(TypePairMatrix.Instance.TryGetOutcome(SqlTypeCategory.VarChar, SqlTypeCategory.NVarChar));

    [Theory]
    [InlineData(SqlTypeCategory.Char, SqlTypeCategory.VarChar, "SQL_Latin1_General_CP1_CI_AS", false)]
    [InlineData(SqlTypeCategory.NChar, SqlTypeCategory.NVarChar, "Latin1_General_CI_AS", false)]
    public void TryGetOutcome_CharVsVarcharFamilyPairs_NeverConvertTheColumn(
        SqlTypeCategory column, SqlTypeCategory other, string collation, bool expectedColumnConverts)
    {

        var outcome = TypePairMatrix.Instance.TryGetOutcome(column, other, collation);

        Assert.NotNull(outcome);
        Assert.Equal(expectedColumnConverts, outcome.ColumnConverts);
    }

    [Theory]
    [InlineData(SqlTypeCategory.VarChar, SqlTypeCategory.Int, "SQL_Latin1_General_CP1_CI_AS", true)]
    [InlineData(SqlTypeCategory.VarChar, SqlTypeCategory.UniqueIdentifier, "Latin1_General_CI_AS", true)]
    [InlineData(SqlTypeCategory.VarChar, SqlTypeCategory.DateTime2, "SQL_Latin1_General_CP1_CI_AS", true)]
    public void TryGetOutcome_CrossFamilyStringColumnVsValue_MatchesOracleProbe(
        SqlTypeCategory column, SqlTypeCategory other, string collation, bool expectedColumnConverts)
    {
        var outcome = TypePairMatrix.Instance.TryGetOutcome(column, other, collation);

        Assert.NotNull(outcome);
        Assert.Equal(expectedColumnConverts, outcome.ColumnConverts);
    }

    [Theory]
    [InlineData(SqlTypeCategory.Int, SqlTypeCategory.VarChar, false)]
    [InlineData(SqlTypeCategory.Bit, SqlTypeCategory.VarChar, false)]
    [InlineData(SqlTypeCategory.UniqueIdentifier, SqlTypeCategory.VarChar, false)]
    public void TryGetOutcome_CrossFamilyNonStringColumnVsStringValue_IsNotCollationKeyed(
        SqlTypeCategory column, SqlTypeCategory other, bool expectedColumnConverts)
    {

        var outcome = TypePairMatrix.Instance.TryGetOutcome(column, other, collationName: null);

        Assert.NotNull(outcome);
        Assert.Equal(expectedColumnConverts, outcome.ColumnConverts);
    }

    [Fact]
    public void TryGetOutcomeAgreeingAcrossCollations_PairWhereEveryCollationAgrees_ReturnsThatOutcome()
    {

        var outcome = TypePairMatrix.Instance.TryGetOutcomeAgreeingAcrossCollations(SqlTypeCategory.NVarChar, SqlTypeCategory.VarChar);

        Assert.NotNull(outcome);
        Assert.False(outcome.ColumnConverts);
    }

    [Fact]
    public void TryGetOutcomeAgreeingAcrossCollations_PairWhereCollationChangesTheOutcome_ReturnsNull()
    {

        var outcome = TypePairMatrix.Instance.TryGetOutcomeAgreeingAcrossCollations(SqlTypeCategory.VarChar, SqlTypeCategory.NVarChar);

        Assert.Null(outcome);
    }
}
