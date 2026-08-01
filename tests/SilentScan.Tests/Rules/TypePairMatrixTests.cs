using SilentScan.Core.Catalog;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Rules;

/// <summary>
/// Phase 0.2 of docs/audit-remediation-plan.md: VerdictClassifier must never fall back to a
/// hand-written family heuristic for same-family cross-category pairs - every decision has to
/// trace back to a real, checked-in oracle probe. These tests pin the loader's contract and a
/// representative sample of the probed data itself, so a corrupted or accidentally-shrunk
/// TypePairMatrix.json fails loudly here rather than silently degrading every affected verdict
/// to UNKNOWN in production.
/// </summary>
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
        Assert.Equal(144, TypePairMatrix.Instance.Entries.Count);

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
        // The matrix key includes collation for string pairs; omitting it must not accidentally
        // match an entry that was only ever probed under a specific collation.
        Assert.Null(TypePairMatrix.Instance.TryGetOutcome(SqlTypeCategory.VarChar, SqlTypeCategory.NVarChar));
}
