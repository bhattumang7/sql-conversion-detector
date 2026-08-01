using SilentScan.Core.Catalog;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Rules;

/// <summary>
/// Direction is the #1 way this kind of tool gets it wrong in public (CLAUDE.md), so these
/// tests are organized around the direction rule first, then the collation nuance.
/// </summary>
public sealed class VerdictClassifierTests
{
    private static readonly Collation SqlCollation = new("SQL_Latin1_General_CP1_CI_AS");
    private static readonly Collation WindowsCollation = new("Latin1_General_CI_AS");

    [Fact]
    public void Classify_VarcharColumnVsNVarcharValue_SqlCollation_ScanForced()
    {
        // CLAUDE.md flagship example: the COLUMN converts (lower precedence), and SQL_*
        // collation means the engine cannot build a dynamic range seek.
        var column = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.NVarChar, Length: 20);

        Assert.Equal(Verdict.ScanForced, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_VarcharColumnVsNVarcharValue_WindowsCollation_RangeSeek()
    {
        var column = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: WindowsCollation);
        var value = new SqlType(SqlTypeCategory.NVarChar, Length: 20);

        Assert.Equal(Verdict.RangeSeek, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_NVarcharColumnVsVarcharValue_DirectionMatters_SeekPreserved()
    {
        // The VALUE converts here (varchar has lower precedence than nvarchar), so the
        // column-side index is untouched - harmless regardless of collation.
        var column = new SqlType(SqlTypeCategory.NVarChar, Length: 20, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 20);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_VarcharColumnUnresolvedCollation_VsNVarcharValue_Unknown()
    {
        var column = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: null);
        var value = new SqlType(SqlTypeCategory.NVarChar, Length: 20);

        Assert.Equal(Verdict.Unknown, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_IntColumnVsBigIntValue_OracleVerifiedSameFamilyWidening_SeekPreserved()
    {
        // Oracle-verified (Phase 4 pilot): int-vs-bigint widening never shows
        // CONVERT_IMPLICIT, even though int has lower precedence than bigint - same-family
        // numeric widening is free. See VerdictClassifier's comment for the full probe set.
        var column = new SqlType(SqlTypeCategory.Int);
        var value = new SqlType(SqlTypeCategory.BigInt);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_DateColumnVsDateTimeValue_OracleVerifiedSameFamilyWidening_SeekPreserved()
    {
        // Real false positive found scanning WideWorldImporters (Phase 4 pilot):
        // WHERE ExpectedDeliveryDate >= @StartingWhen (date column, datetime param).
        var column = new SqlType(SqlTypeCategory.Date);
        var value = new SqlType(SqlTypeCategory.DateTime);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_BigIntColumnVsIntValue_ValueConverts_SeekPreserved()
    {
        var column = new SqlType(SqlTypeCategory.BigInt);
        var value = new SqlType(SqlTypeCategory.Int);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_SameCategorySameCollation_SeekPreserved()
    {
        var column = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 50, Collation: SqlCollation);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_SameCategoryDifferentCollation_Unknown()
    {
        var column = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: WindowsCollation);

        Assert.Equal(Verdict.Unknown, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_SameCategoryNoCollationInvolved_SeekPreserved()
    {
        var column = new SqlType(SqlTypeCategory.Int);
        var value = new SqlType(SqlTypeCategory.Int);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_NullColumnType_Unknown()
    {
        Assert.Equal(Verdict.Unknown, VerdictClassifier.Classify(null, new SqlType(SqlTypeCategory.Int)));
    }

    [Fact]
    public void Classify_NullOtherType_Unknown()
    {
        Assert.Equal(Verdict.Unknown, VerdictClassifier.Classify(new SqlType(SqlTypeCategory.Int), null));
    }

    [Fact]
    public void Classify_DateTimeColumnVsVarcharValue_DateTimeOutranksVarchar_SeekPreserved()
    {
        // datetime sits ABOVE the string family in T-SQL's precedence list, so the VALUE
        // (varchar) converts, not the column - this is the official-docs-verified case that
        // caught a real ordering bug in SqlTypeCategory (Time was misplaced after
        // DateTimeOffset instead of right after Float).
        var column = new SqlType(SqlTypeCategory.DateTime);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 10);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_VarcharColumnVsDateTimeValue_ColumnConverts_ScanForced()
    {
        // The reverse direction: the varchar COLUMN converts to datetime.
        var column = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.DateTime);

        Assert.Equal(Verdict.ScanForced, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_BitColumnVsIntLiteral_OracleVerifiedNoConversion_SeekPreserved()
    {
        // Real false positive found scanning WideWorldImporters (Phase 4 pilot):
        // WHERE IsPermittedToLogon = 0 against a BIT column. Confirmed against the real
        // SQL Server oracle that this produces no CONVERT_IMPLICIT at all - see
        // VerdictClassifier's comment for the full set of oracle probes.
        var column = new SqlType(SqlTypeCategory.Bit);
        var value = new SqlType(SqlTypeCategory.Int);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_BitColumnVsBigIntValue_SeekPreserved()
    {
        var column = new SqlType(SqlTypeCategory.Bit);
        var value = new SqlType(SqlTypeCategory.BigInt);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_BitColumnVsFloatValue_SeekPreserved()
    {
        var column = new SqlType(SqlTypeCategory.Bit);
        var value = new SqlType(SqlTypeCategory.Float);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_BitColumnVsVarcharValue_ValueConvertsNotColumn_SeekPreserved()
    {
        // Bit outranks the string family, so the VALUE converts here - and this is a genuine
        // conversion (confirmed CONVERT_IMPLICIT on the parameter side against the oracle),
        // just not one that affects the column's seekability.
        var column = new SqlType(SqlTypeCategory.Bit);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 5);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_CharColumnVsNCharValue_SqlCollation_ScanForced()
    {
        var column = new SqlType(SqlTypeCategory.Char, Length: 10, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.NChar, Length: 10);

        Assert.Equal(Verdict.ScanForced, VerdictClassifier.Classify(column, value));
    }
}
