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
        // Oracle-verified (docs/audit-remediation-plan.md Phase 0.2 type-pair matrix): int-vs-
        // bigint widening never shows CONVERT_IMPLICIT, even though int has lower precedence
        // than bigint - but this is a per-pair fact, not a blanket "numeric widening is free"
        // rule (see the IntColumnVsRealValue test below for the pair where it isn't).
        var column = new SqlType(SqlTypeCategory.Int);
        var value = new SqlType(SqlTypeCategory.BigInt);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_IntColumnVsRealValue_ExactVsApproximateNumeric_RangeSeek()
    {
        // The type-pair matrix found this: unlike int-vs-bigint above, comparing an INT column
        // against a REAL/FLOAT value DOES produce a column-side CONVERT_IMPLICIT (int's full
        // domain isn't exactly representable in a 4-byte float, so the optimizer can't build a
        // safe range without converting the column) - a same-numeric-family pair that the old
        // "numeric widening is always free" heuristic would have wrongly called SeekPreserved.
        // The plan also contains a GetRangeThroughConvert node for this pair, so the honest
        // verdict is RangeSeek (dynamic seek still possible), not the more severe ScanForced -
        // the matrix's DynamicRangeSeekAvailable flag must not be discarded.
        var column = new SqlType(SqlTypeCategory.Int);
        var value = new SqlType(SqlTypeCategory.Real);

        Assert.Equal(Verdict.RangeSeek, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_TinyIntColumnVsRealValue_DomainFitsExactly_SeekPreserved()
    {
        // The counterpart to the Int-vs-Real case: TinyInt's entire domain (0-255) is exactly
        // representable in a float, so the optimizer elides the conversion here even though
        // it does not for Int/BigInt/Money/Decimal against the same Real type - confirming the
        // matrix is keyed per exact category pair, not a coarse numeric/numeric rule.
        var column = new SqlType(SqlTypeCategory.TinyInt);
        var value = new SqlType(SqlTypeCategory.Real);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_TimeColumnVsDateValue_NotComparableAtAll_Unknown()
    {
        // The Phase 0.2 probe found TIME and every other date/time category (DATE,
        // SMALLDATETIME, DATETIME, DATETIME2, DATETIMEOFFSET) are not implicitly comparable at
        // all - SQL Server rejects the comparison at compile time ("data types time and date
        // are incompatible"). Not a seek/scan question; UNKNOWN, never guessed.
        var column = new SqlType(SqlTypeCategory.Time);
        var value = new SqlType(SqlTypeCategory.Date);

        Assert.Equal(Verdict.Unknown, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_UnprobedSameFamilyPair_Unknown()
    {
        // SmallMoney vs SmallMoney is same-category (handled elsewhere); this constructs a
        // category pair the matrix has no entry for at all, to prove the "not probed => never
        // guessed" contract independent of any specific real pair going unprobed by accident.
        var outcome = TypePairMatrix.Instance.TryGetOutcome(SqlTypeCategory.Bit, SqlTypeCategory.Bit);
        Assert.Null(outcome);
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

    [Fact]
    public void Classify_CharColumnVsVarcharValue_SameComparisonType_SeekPreserved()
    {
        // char and varchar (and, symmetrically, nchar and nvarchar) are the SAME comparison
        // type in SQL Server - no CONVERT_IMPLICIT on either side, seek fully preserved. The
        // classifier used to answer this from raw precedence + collation alone and disagreed
        // with its own oracle-probed matrix entry for this exact cell (ColumnConverts=false).
        var column = new SqlType(SqlTypeCategory.Char, Length: 10, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 10);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_NVarcharColumnVsNCharValue_SameComparisonType_SeekPreserved()
    {
        var column = new SqlType(SqlTypeCategory.NVarChar, Length: 10, Collation: WindowsCollation);
        var value = new SqlType(SqlTypeCategory.NChar, Length: 10);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_VarcharColumnVsIntValue_ColumnConverts_ScanForced()
    {
        // CLAUDE.md flagship cross-family example: `varcharCol = 5`.
        var column = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.Int);

        Assert.Equal(Verdict.ScanForced, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_IntColumnVsVarcharValue_ColumnOutranksValue_SeekPreserved()
    {
        // The reverse of the flagship example: int outranks varchar, so the VALUE converts.
        var column = new SqlType(SqlTypeCategory.Int);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 10);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_VarcharColumnVsGuidValue_ColumnConverts_ScanForced()
    {
        var column = new SqlType(SqlTypeCategory.VarChar, Length: 36, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.UniqueIdentifier);

        Assert.Equal(Verdict.ScanForced, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_SqlVariantColumn_NeverParticipatesInPrecedence_Unknown()
    {
        var column = new SqlType(SqlTypeCategory.SqlVariant);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: SqlCollation);

        Assert.Equal(Verdict.Unknown, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_XmlColumn_NotComparable_Unknown()
    {
        var column = new SqlType(SqlTypeCategory.Xml);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: SqlCollation);

        Assert.Equal(Verdict.Unknown, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_XmlColumnVsXmlValue_SameCategoryStillOutOfModel_Unknown()
    {
        // Regression: the out-of-model check must run BEFORE the same-category branch. xml
        // is not comparable with '=' at all, so "same category" must never fall through to
        // ClassifySameCategory's SeekPreserved default - that would report a seek-preserving
        // verdict for a comparison the engine doesn't even support.
        var column = new SqlType(SqlTypeCategory.Xml);
        var value = new SqlType(SqlTypeCategory.Xml);

        Assert.Equal(Verdict.Unknown, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_SqlVariantColumnVsSqlVariantValue_SameCategoryStillOutOfModel_Unknown()
    {
        var column = new SqlType(SqlTypeCategory.SqlVariant);
        var value = new SqlType(SqlTypeCategory.SqlVariant);

        Assert.Equal(Verdict.Unknown, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_TextColumnVsTextValue_SameCategoryStillOutOfModel_Unknown()
    {
        var column = new SqlType(SqlTypeCategory.Text);
        var value = new SqlType(SqlTypeCategory.Text);

        Assert.Equal(Verdict.Unknown, VerdictClassifier.Classify(column, value));
    }

    [Theory]
    [MemberData(nameof(AllMatrixEntries))]
    public void Classify_NeverDisagreesWithItsOwnOracleProbedMatrix(
        SqlTypeCategory columnCategory, SqlTypeCategory otherCategory, string? collationName, bool columnConverts, bool compileFailed, bool dynamicRangeSeekAvailable)
    {
        // Guard rail for the architectural invariant: the matrix is the SOLE verdict
        // authority. For every probed cell, feeding the classifier the same category pair
        // (and collation, for string-family cells) must reproduce exactly what the cell says
        // - the classifier must never have its own opinion that drifts from the data it is
        // supposed to be a pure lookup over. Unpacked to primitive theory parameters (rather
        // than the TypePairOutcome record itself) because xUnit's Test Explorer enumeration
        // needs each data row to be independently serializable, which a plain record isn't.
        var entry = new TypePairOutcome(columnCategory, otherCategory, collationName, columnConverts, compileFailed, dynamicRangeSeekAvailable);
        var columnType = BuildProbedType(entry.ColumnCategory, entry.CollationName);
        var otherType = BuildProbedType(entry.OtherCategory, entry.CollationName);

        var actual = VerdictClassifier.Classify(columnType, otherType);

        if (entry.CompileFailed)
        {
            Assert.Equal(Verdict.Unknown, actual);
        }
        else if (!entry.ColumnConverts)
        {
            Assert.Equal(Verdict.SeekPreserved, actual);
        }
        else
        {
            Assert.Equal(entry.DynamicRangeSeekAvailable ? Verdict.RangeSeek : Verdict.ScanForced, actual);
        }
    }

    public static TheoryData<SqlTypeCategory, SqlTypeCategory, string?, bool, bool, bool> AllMatrixEntries()
    {
        var data = new TheoryData<SqlTypeCategory, SqlTypeCategory, string?, bool, bool, bool>();
        foreach (var e in TypePairMatrix.Instance.Entries)
        {
            data.Add(e.ColumnCategory, e.OtherCategory, e.CollationName, e.ColumnConverts, e.CompileFailed, e.DynamicRangeSeekAvailable);
        }

        return data;
    }

    private static SqlType BuildProbedType(SqlTypeCategory category, string? collationName)
    {
        var isStringFamily = category is SqlTypeCategory.Char or SqlTypeCategory.VarChar
            or SqlTypeCategory.NChar or SqlTypeCategory.NVarChar;
        return new SqlType(category, Length: isStringFamily ? 20 : null, Collation: collationName is null ? null : new Collation(collationName));
    }
}
