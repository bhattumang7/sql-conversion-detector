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
        // 514 numeric/datetime/string/binary-family entries (Roadmap Phase A3) plus 276 more
        // from widening cross-family probing to cover every pair across numeric/date-time/binary
        // together (not just within one family) and Binary/VarBinary/Timestamp vs string in both
        // directions - previously-unprobed cells found auditing a real production database's
        // Unknown-verdict rate (CLAUDE.md: never guess a cross-category verdict from the
        // precedence list alone - every cell must trace back to a real oracle probe).
        Assert.Equal(790, TypePairMatrix.Instance.Entries.Count);

    [Fact]
    public void Instance_ProbesEveryDeclaredCollationWithTheSameEntryCount()
    {
        // Guards the two-collation-generalization concern directly (an audit finding: only two
        // Windows-family representatives made TryGetOutcomeAgreeingAcrossCollations' "every
        // probed collation agreed" claim a thin one) - every collation TypeMatrixGenerator
        // declares must actually have landed in the checked-in matrix, with the SAME per-
        // collation entry count, or a future regeneration silently dropping one collation's
        // probes would slip through unnoticed.
        var entries = TypePairMatrix.Instance.Entries;
        var byCollation = entries.Where(e => e.CollationName is not null).ToLookup(e => e.CollationName);

        Assert.Equal(SilentScan.Verify.Oracle.TypeMatrixGenerator.Collations.Count, byCollation.Count);
        foreach (var collation in SilentScan.Verify.Oracle.TypeMatrixGenerator.Collations)
        {
            Assert.True(byCollation.Contains(collation), $"matrix has no entries for collation '{collation}'");
            // +12 per collation (4 StringFamily categories x 3 newly-added Binary/VarBinary/
            // Timestamp CrossFamilyOther entries, string-column-vs-binary-value direction) since
            // BinaryFamily joined CrossFamilyOther.
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
        // The matrix key includes collation for string pairs; omitting it must not accidentally
        // match an entry that was only ever probed under a specific collation.
        Assert.Null(TypePairMatrix.Instance.TryGetOutcome(SqlTypeCategory.VarChar, SqlTypeCategory.NVarChar));

    [Theory]
    [InlineData(SqlTypeCategory.Char, SqlTypeCategory.VarChar, "SQL_Latin1_General_CP1_CI_AS", false)]
    [InlineData(SqlTypeCategory.NChar, SqlTypeCategory.NVarChar, "Latin1_General_CI_AS", false)]
    public void TryGetOutcome_CharVsVarcharFamilyPairs_NeverConvertTheColumn(
        SqlTypeCategory column, SqlTypeCategory other, string collation, bool expectedColumnConverts)
    {
        // The bug this guards: char/varchar (and nchar/nvarchar) are the same comparison type
        // in SQL Server - no CONVERT_IMPLICIT on either side. VerdictClassifier used to bypass
        // the matrix for string pairs entirely and report ScanForced here, contradicting this
        // very entry.
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
        // Non-string columns aren't collation-sensitive, so this direction is probed once and
        // recorded with a null collation key - matching how VerdictClassifier looks these up.
        var outcome = TypePairMatrix.Instance.TryGetOutcome(column, other, collationName: null);

        Assert.NotNull(outcome);
        Assert.Equal(expectedColumnConverts, outcome.ColumnConverts);
    }

    [Fact]
    public void TryGetOutcomeAgreeingAcrossCollations_PairWhereEveryCollationAgrees_ReturnsThatOutcome()
    {
        // nvarchar column vs varchar value never converts the column regardless of collation -
        // a precedence-direction fact, not a collation-dependent one. This lets a column with
        // unresolved collation still get a real (non-guessed) verdict for pairs like this one.
        var outcome = TypePairMatrix.Instance.TryGetOutcomeAgreeingAcrossCollations(SqlTypeCategory.NVarChar, SqlTypeCategory.VarChar);

        Assert.NotNull(outcome);
        Assert.False(outcome.ColumnConverts);
    }

    [Fact]
    public void TryGetOutcomeAgreeingAcrossCollations_PairWhereCollationChangesTheOutcome_ReturnsNull()
    {
        // varchar column vs nvarchar value: ScanForced under SQL_*, RangeSeek under Windows -
        // collation genuinely changes the answer, so this must NOT silently pick one.
        var outcome = TypePairMatrix.Instance.TryGetOutcomeAgreeingAcrossCollations(SqlTypeCategory.VarChar, SqlTypeCategory.NVarChar);

        Assert.Null(outcome);
    }
}
