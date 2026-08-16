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
    public void Classify_VarcharColumnVsNVarcharValue_UnprobedSqlFamilyCollation_FallsBackToScanForced()
    {
        // SQL_Latin1_General_CP1_CS_AS was never itself probed by TypeMatrixGenerator - only the
        // CI_AS variant was - but it shares the same SQL_* family, which is the axis this outcome
        // actually turns on (CLAUDE.md: "Collation is a first-class input... SQL_* collations ->
        // ScanForced"). A resolved-but-unprobed collation must fall back to the family's probed
        // representative(s) rather than reporting Unknown for a fact the matrix already knows.
        var column = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CS_AS"));
        var value = new SqlType(SqlTypeCategory.NVarChar, Length: 20);

        Assert.Equal(Verdict.ScanForced, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_VarcharColumnVsNVarcharValue_UnprobedWindowsFamilyCollation_FallsBackToRangeSeek()
    {
        // French_CI_AS is a Windows-family collation (does not start with "SQL_") that was never
        // itself probed; the matrix DOES probe three other Windows-family representatives
        // (Latin1_General_CI_AS, the UTF-8 variant, and the _BIN2 variant specifically to stress
        // this generalization - TypeMatrixGenerator.Collations' own remarks) and they all agree
        // on RangeSeek, so this resolved-but-unprobed Windows collation should generalize to the
        // same answer rather than falling to Unknown.
        var column = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("French_CI_AS"));
        var value = new SqlType(SqlTypeCategory.NVarChar, Length: 20);

        Assert.Equal(Verdict.RangeSeek, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_ResolvedCollation_NeverLessInformativeThanUnresolvedCollation()
    {
        // Regression for the audit-found inversion: previously, a RESOLVED-but-not-exactly-
        // probed collation (French_CI_AS) fell straight to Unknown, while an UNRESOLVED collation
        // on the exact same pair could still get a real verdict via
        // TryGetOutcomeAgreeingAcrossCollations - knowing MORE about the column produced LESS
        // information. Assert the resolved case now at least matches what the unresolved case
        // reports for a pair where every probed collation (both families) happens to agree.
        var unresolvedColumn = new SqlType(SqlTypeCategory.NVarChar, Length: 20);
        var resolvedColumn = new SqlType(SqlTypeCategory.NVarChar, Length: 20, Collation: new Collation("French_CI_AS"));
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 20);

        var unresolvedVerdict = VerdictClassifier.Classify(unresolvedColumn, value);
        var resolvedVerdict = VerdictClassifier.Classify(resolvedColumn, value);

        Assert.NotEqual(Verdict.Unknown, unresolvedVerdict);
        Assert.Equal(unresolvedVerdict, resolvedVerdict);
    }

    [Fact]
    public void Classify_VarcharColumnLikeNVarcharVariable_WindowsCollation_ScanForced()
    {
        // Oracle-verified directly (Docker SQL Server): the matrix's RangeSeek cells were probed
        // as column-vs-variable under `=` - a LIKE predicate whose pattern is NOT a literal loses
        // the dynamic range seek (the pattern's shape is unknown at compile time, so the optimizer
        // can't build the same range rewrite an equality or a literal-pattern LIKE gets), and is
        // genuinely ScanForced instead of RangeSeek. This was previously misclassified: the matrix
        // was probed with `=` only and applied uniformly to every operator.
        var column = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: WindowsCollation);
        var value = new SqlType(SqlTypeCategory.NVarChar, Length: 20);

        Assert.Equal(Verdict.ScanForced, VerdictClassifier.Classify(column, value, operatorText: "LIKE"));
    }

    [Fact]
    public void Classify_VarcharColumnLikeNVarcharLiteralPattern_WindowsCollation_StillRangeSeek()
    {
        // Near-miss: a LIKE pattern that IS a literal (its shape is known at compile time) keeps
        // the dynamic range seek, agreeing with `=` - oracle-verified directly. otherIsLiteral
        // is what distinguishes this from the non-literal-pattern case above.
        var column = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: WindowsCollation);
        var value = new SqlType(SqlTypeCategory.NVarChar, Length: 20);

        Assert.Equal(Verdict.RangeSeek, VerdictClassifier.Classify(column, value, otherIsLiteral: true, operatorText: "LIKE"));
    }

    [Fact]
    public void Classify_VarcharColumnVsNVarcharColumn_WindowsCollation_StillRangeSeek()
    {
        // Column-vs-column (e.g. a JOIN predicate) was investigated as a possible correction
        // alongside the LIKE one below, but the divergence turned out confounded: whether the
        // dynamic range seek disappears from the plan depends on whether the OTHER column is
        // ALSO indexed (both indexed: it disappears; only the classified side indexed - the far
        // more common real shape - it's present, agreeing with the matrix). That's a plan-shape-
        // dependent fact, not a stable type-pair one, so no correction is made here - column-vs-
        // column keeps the matrix's plain answer. See VerdictClassifier.Classify's own remarks.
        var column = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: WindowsCollation);
        var otherColumn = new SqlType(SqlTypeCategory.NVarChar, Length: 20, Collation: WindowsCollation);

        Assert.Equal(Verdict.RangeSeek, VerdictClassifier.Classify(column, otherColumn));
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
    public void Classify_TimeColumnVsDateValue_NotComparableAtAll_OperandClash()
    {
        // The Phase 0.2 probe found TIME and every other date/time category (DATE,
        // SMALLDATETIME, DATETIME, DATETIME2, DATETIMEOFFSET) are not implicitly comparable at
        // all - SQL Server rejects the comparison at compile time ("data types time and date
        // are incompatible"). Not a seek/scan question; this is a probed, empirically-confirmed
        // fact (Roadmap Phase A3: OperandClash), not an absence of data - so it is no longer
        // folded into the generic Unknown bucket.
        var column = new SqlType(SqlTypeCategory.Time);
        var value = new SqlType(SqlTypeCategory.Date);

        Assert.Equal(Verdict.OperandClash, VerdictClassifier.Classify(column, value));
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
    public void Classify_SameCategoryDifferentCollation_OtherNotProvenLiteral_OperandClash()
    {
        // otherIsLiteral defaults to false - a real column, a CAST/CONVERT result, or anything
        // else not provably a source-text literal is "implicit" coercibility, and comparing two
        // differing "implicit" collations does not compile at all (Msg 468, oracle-verified
        // directly: Docker SQL Server, a CAST result inheriting a foreign column's collation
        // compared against a target column of the same category raises Msg 468 identically to
        // two real columns). Confirmed compile failure, not a guessed verdict.
        var column = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: WindowsCollation);

        Assert.Equal(Verdict.OperandClash, VerdictClassifier.Classify(column, value));
        Assert.Equal(Verdict.OperandClash, VerdictClassifier.Classify(column, value, otherIsLiteral: false));
    }

    [Fact]
    public void Classify_CrossCategoryStringPair_DifferentCollation_OtherNotProvenLiteral_OperandClash()
    {
        // The gap this fix closes: CHAR vs VARCHAR is a different type CATEGORY, but a genuine
        // collation mismatch does not care about category - oracle-verified directly (Docker SQL
        // Server): CHAR column vs VARCHAR column with differing collations raises Msg 468
        // identically to VARCHAR vs VARCHAR with differing collations. Before this fix, the
        // category-equality gate meant this fell through to the type-pair matrix and reported
        // whatever Char|VarChar's same-collation cell says (SeekPreserved) - a compile error
        // reported as clean, the worst kind of false negative for this tool.
        var column = new SqlType(SqlTypeCategory.Char, Length: 10, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: WindowsCollation);

        Assert.Equal(Verdict.OperandClash, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_CrossCategoryStringPair_DifferentCollation_OtherIsLiteral_NotOperandClash()
    {
        // A literal never conflicts (always "coercible default"), so cross-category collation
        // differences don't matter for it either - falls through to the normal matrix-driven
        // cross-category verdict (Char vs VarChar, both string columns share the same category
        // family behavior as VarChar vs VarChar: the column does not convert regardless of
        // collation, since Char's own COLLATE is what a differing-collation literal would force
        // convert, and Char vs VarChar same-width never converts the column - oracle-probed).
        var column = new SqlType(SqlTypeCategory.Char, Length: 10, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: WindowsCollation);

        Assert.NotEqual(Verdict.OperandClash, VerdictClassifier.Classify(column, value, otherIsLiteral: true));
    }

    [Fact]
    public void Classify_CrossCategoryStringPair_SameCollation_NoConflict()
    {
        // Matching collations never conflict, category difference or not (oracle-verified:
        // CHAR vs VARCHAR joins cleanly when both sides share one collation) - falls through to
        // the ordinary matrix-driven cross-category verdict, not OperandClash.
        var column = new SqlType(SqlTypeCategory.Char, Length: 10, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: SqlCollation);

        Assert.NotEqual(Verdict.OperandClash, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_SameCategoryDifferentCollation_OtherIsLiteral_ScanForced()
    {
        // Oracle-verified directly (Docker SQL Server, compile-only SHOWPLAN_XML): a literal is
        // T-SQL's "coercible default" tier and never conflicts, so a differing collation there
        // forces CONVERT_IMPLICIT onto the COLUMN even though nothing about the column's own
        // syntax changed - ScanForced, never RangeSeek (that optimization is cross-category-only).
        var column = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: WindowsCollation);

        Assert.Equal(Verdict.ScanForced, VerdictClassifier.Classify(column, value, otherIsLiteral: true));
    }

    [Fact]
    public void Classify_SameCategoryNoCollationInvolved_SeekPreserved()
    {
        var column = new SqlType(SqlTypeCategory.Int);
        var value = new SqlType(SqlTypeCategory.Int);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_SameCategoryFacetDifference_VarcharShorterColumnLongerValue_SeekPreserved()
    {
        // Oracle-verified directly (Docker SQL Server, compile-only SHOWPLAN_XML): varchar(10)
        // column vs a varchar(100)/varchar(max) value seeks cleanly, no CONVERT_IMPLICIT
        // anywhere - length differences within the same category never defeat sargability.
        var column = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 100, Collation: SqlCollation);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_SameCategoryFacetDifference_DecimalDifferingPrecisionAndScale_SeekPreserved()
    {
        // Oracle-verified: decimal(10,2) column vs decimal(9,8)/decimal(38,10) value, and vs a
        // high-precision literal (1.23456789), all seek cleanly - precision/scale differences
        // within the same category never defeat sargability either.
        var column = new SqlType(SqlTypeCategory.Decimal, Precision: 10, Scale: 2);
        var value = new SqlType(SqlTypeCategory.Decimal, Precision: 9, Scale: 8);

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
    public void Classify_SqlVariantColumnVsInModelValue_HighestPrecedence_SeekPreserved()
    {
        // sql_variant is T-SQL's highest-precedence type - oracle-verified (see
        // VerdictClassifier's own doc comment): the sql_variant COLUMN never converts, the
        // in-model value does, so the column keeps its seek.
        var column = new SqlType(SqlTypeCategory.SqlVariant);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: SqlCollation);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_InModelColumnVsSqlVariantValue_HighestPrecedence_ScanForced()
    {
        // Reverse direction: sql_variant is the highest-precedence side regardless of which
        // operand it's on, so an in-model COLUMN compared against a sql_variant value always
        // converts - oracle-verified.
        var column = new SqlType(SqlTypeCategory.Int);
        var value = new SqlType(SqlTypeCategory.SqlVariant);

        Assert.Equal(Verdict.ScanForced, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_SqlVariantColumnVsSqlVariantValue_BothOutOfModel_Unknown()
    {
        // Two sql_variant operands: runtime comparison semantics depend on the boxed base type,
        // not resolvable statically - falls through to the general out-of-model Unknown.
        var column = new SqlType(SqlTypeCategory.SqlVariant);
        var value = new SqlType(SqlTypeCategory.SqlVariant);

        Assert.Equal(Verdict.Unknown, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_SqlVariantColumnVsXmlValue_BothOutOfModel_Unknown()
    {
        // sql_variant paired with ANOTHER out-of-model category (not just an in-model one) must
        // still fall through to Unknown rather than the new precedence branch.
        var column = new SqlType(SqlTypeCategory.SqlVariant);
        var value = new SqlType(SqlTypeCategory.Xml);

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

    [Fact]
    public void Classify_UniqueIdentifierColumnVsVarcharValue_ValueConverts_SeekPreserved()
    {
        // Oracle-verified (TypeMatrixGenerator regeneration, 2026-08-03): this cell was
        // previously reported as a fabricated OperandClash because the generator forgot to
        // deploy a T_UniqueIdentifier probe table, so every guid-as-column probe threw
        // "Invalid object name" and a blanket `catch (SqlException)` recorded it as
        // CompileFailed=true. uniqueidentifier actually outranks the string types in T-SQL
        // precedence, so a varchar VALUE compared against a uniqueidentifier COLUMN converts
        // the value, not the column - the seek survives.
        var column = new SqlType(SqlTypeCategory.UniqueIdentifier);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 36);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_VarBinaryColumnVsTimestampValue_OracleVerified_ScanForced()
    {
        // Roadmap Phase A3: previously absent from the matrix entirely (Unknown regardless of
        // direction) - a rowversion/timestamp concurrency token compared against a VARBINARY
        // variable is a ubiquitous optimistic-concurrency pattern.
        var column = new SqlType(SqlTypeCategory.VarBinary, Length: 8);
        var value = new SqlType(SqlTypeCategory.Timestamp);

        Assert.Equal(Verdict.ScanForced, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_TimestampColumnVsVarBinaryValue_DirectionMatters_SeekPreserved()
    {
        var column = new SqlType(SqlTypeCategory.Timestamp);
        var value = new SqlType(SqlTypeCategory.VarBinary, Length: 8);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_BinaryColumnVsVarBinaryValue_SameComparisonType_SeekPreserved()
    {
        // Mirrors the already-established char/varchar precedent: binary and varbinary are the
        // same comparison type in SQL Server, despite being distinct SqlTypeCategory values.
        var column = new SqlType(SqlTypeCategory.Binary, Length: 16);
        var value = new SqlType(SqlTypeCategory.VarBinary, Length: 16);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
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
            Assert.Equal(Verdict.OperandClash, actual);
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

    [Fact]
    public void ClassifyWithReason_UnresolvedColumnType_ReasonIsOperandTypeUnresolved()
    {
        var (verdict, reason) = VerdictClassifier.ClassifyWithReason(null, new SqlType(SqlTypeCategory.Int));

        Assert.Equal(Verdict.Unknown, verdict);
        Assert.Equal("operand-type-unresolved", reason);
    }

    [Fact]
    public void ClassifyWithReason_UnresolvedOtherType_ReasonIsOperandTypeUnresolved()
    {
        var (verdict, reason) = VerdictClassifier.ClassifyWithReason(new SqlType(SqlTypeCategory.Int), null);

        Assert.Equal(Verdict.Unknown, verdict);
        Assert.Equal("operand-type-unresolved", reason);
    }

    [Fact]
    public void ClassifyWithReason_OutOfModelColumnCategory_ReasonNamesTheCategory()
    {
        var column = new SqlType(SqlTypeCategory.Xml);
        var value = new SqlType(SqlTypeCategory.Int);

        var (verdict, reason) = VerdictClassifier.ClassifyWithReason(column, value);

        Assert.Equal(Verdict.Unknown, verdict);
        Assert.Equal("out-of-model-category:Xml", reason);
    }

    [Fact]
    public void ClassifyWithReason_OutOfModelOtherCategory_ReasonNamesTheCategory()
    {
        // sql_variant is no longer a usable example here - it now participates in the standard
        // precedence rule (see the dedicated sql_variant tests above) - xml stays genuinely
        // out-of-model (not even comparable with '=').
        var column = new SqlType(SqlTypeCategory.Int);
        var value = new SqlType(SqlTypeCategory.Xml);

        var (verdict, reason) = VerdictClassifier.ClassifyWithReason(column, value);

        Assert.Equal(Verdict.Unknown, verdict);
        Assert.Equal("out-of-model-category:Xml", reason);
    }

    [Fact]
    public void ClassifyWithReason_VarcharColumnUnresolvedCollation_ReasonIsNoProbedMatrixCell()
    {
        var column = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: null);
        var value = new SqlType(SqlTypeCategory.NVarChar, Length: 20);

        var (verdict, reason) = VerdictClassifier.ClassifyWithReason(column, value);

        Assert.Equal(Verdict.Unknown, verdict);
        Assert.Equal("no-probed-matrix-cell", reason);
    }

    [Fact]
    public void ClassifyWithReason_NonUnknownVerdict_ReasonIsNull()
    {
        var column = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("Latin1_General_CI_AS"));
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("Latin1_General_CI_AS"));

        var (verdict, reason) = VerdictClassifier.ClassifyWithReason(column, value);

        Assert.Equal(Verdict.SeekPreserved, verdict);
        Assert.Null(reason);
    }

    [Fact]
    public void Classify_BoundedColumnVsMaxValue_SqlCollation_RangeSeek()
    {
        // Oracle-verified WITH REAL DATA (5,000 rows - an empty table is a documented trap for
        // this exact probe shape elsewhere in this codebase): SQL_* collation compiles a
        // bounded-vs-MAX same-category comparison to GetRangeWithMismatchedTypes, a real Index
        // Seek with actual SeekPredicates/RangeColumns range bounds - the collation-asymmetry
        // GetRangeThroughConvert has does NOT apply here, since a same-category MAX mismatch
        // never crosses character sets.
        var column = new SqlType(SqlTypeCategory.VarChar, Length: 50, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"));
        var value = new SqlType(SqlTypeCategory.VarChar, IsMax: true, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"));

        Assert.Equal(Verdict.RangeSeek, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_BoundedColumnVsMaxValue_WindowsCollation_RangeSeek()
    {
        // Oracle-verified: Windows collation compiles the identical shape identically to the
        // SQL_* case above - both use GetRangeWithMismatchedTypes, confirming this is
        // collation-independent.
        var column = new SqlType(SqlTypeCategory.VarChar, Length: 50, Collation: new Collation("Latin1_General_CI_AS"));
        var value = new SqlType(SqlTypeCategory.VarChar, IsMax: true, Collation: new Collation("Latin1_General_CI_AS"));

        Assert.Equal(Verdict.RangeSeek, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_BoundedColumnVsMaxValue_UnresolvedCollation_StillRangeSeek()
    {
        // Deliberately does not depend on collation resolution at all - unlike
        // GetRangeThroughConvert's own collation-family dependency.
        var column = new SqlType(SqlTypeCategory.VarChar, Length: 50, Collation: null);
        var value = new SqlType(SqlTypeCategory.VarChar, IsMax: true, Collation: null);

        Assert.Equal(Verdict.RangeSeek, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_BothMaxSameCategory_SameCollation_SeekPreserved()
    {
        // Both sides MAX (no mismatch at all) - unaffected by the new check, same as before.
        var column = new SqlType(SqlTypeCategory.VarChar, IsMax: true, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"));
        var value = new SqlType(SqlTypeCategory.VarChar, IsMax: true, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"));

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }
}
