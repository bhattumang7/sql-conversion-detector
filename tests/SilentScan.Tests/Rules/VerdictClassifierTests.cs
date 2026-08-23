using SilentScan.Core.Catalog;
using SilentScan.Core.Rules;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Rules;

public sealed class VerdictClassifierTests
{
    private static readonly Collation SqlCollation = new("SQL_Latin1_General_CP1_CI_AS");
    private static readonly Collation WindowsCollation = new("Latin1_General_CI_AS");

    [Fact]
    public void Classify_VarcharColumnVsNVarcharValue_SqlCollation_ScanForced()
    {

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

        var column = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CS_AS"));
        var value = new SqlType(SqlTypeCategory.NVarChar, Length: 20);

        Assert.Equal(Verdict.ScanForced, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_VarcharColumnVsNVarcharValue_UnprobedWindowsFamilyCollation_FallsBackToRangeSeek()
    {

        var column = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("French_CI_AS"));
        var value = new SqlType(SqlTypeCategory.NVarChar, Length: 20);

        Assert.Equal(Verdict.RangeSeek, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_ResolvedCollation_NeverLessInformativeThanUnresolvedCollation()
    {

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

        var column = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: WindowsCollation);
        var value = new SqlType(SqlTypeCategory.NVarChar, Length: 20);

        Assert.Equal(Verdict.ScanForced, VerdictClassifier.Classify(column, value, operatorText: "LIKE"));
    }

    [Fact]
    public void Classify_VarcharColumnLikeNVarcharLiteralPattern_WindowsCollation_StillRangeSeek()
    {

        var column = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: WindowsCollation);
        var value = new SqlType(SqlTypeCategory.NVarChar, Length: 20);

        Assert.Equal(Verdict.RangeSeek, VerdictClassifier.Classify(column, value, otherIsLiteral: true, operatorText: "LIKE"));
    }

    [Fact]
    public void Classify_VarcharColumnVsNVarcharColumn_WindowsCollation_StillRangeSeek()
    {

        var column = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: WindowsCollation);
        var otherColumn = new SqlType(SqlTypeCategory.NVarChar, Length: 20, Collation: WindowsCollation);

        Assert.Equal(Verdict.RangeSeek, VerdictClassifier.Classify(column, otherColumn));
    }

    [Fact]
    public void Classify_NVarcharColumnVsVarcharValue_DirectionMatters_SeekPreserved()
    {

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

        var column = new SqlType(SqlTypeCategory.Int);
        var value = new SqlType(SqlTypeCategory.BigInt);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_IntColumnVsRealValue_ExactVsApproximateNumeric_RangeSeek()
    {

        var column = new SqlType(SqlTypeCategory.Int);
        var value = new SqlType(SqlTypeCategory.Real);

        Assert.Equal(Verdict.RangeSeek, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_TinyIntColumnVsRealValue_DomainFitsExactly_SeekPreserved()
    {

        var column = new SqlType(SqlTypeCategory.TinyInt);
        var value = new SqlType(SqlTypeCategory.Real);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_TimeColumnVsDateValue_NotComparableAtAll_OperandClash()
    {

        var column = new SqlType(SqlTypeCategory.Time);
        var value = new SqlType(SqlTypeCategory.Date);

        Assert.Equal(Verdict.OperandClash, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_UnprobedSameFamilyPair_Unknown()
    {

        var outcome = TypePairMatrix.Instance.TryGetOutcome(SqlTypeCategory.Bit, SqlTypeCategory.Bit);
        Assert.Null(outcome);
    }

    [Fact]
    public void Classify_DateColumnVsDateTimeValue_OracleVerifiedSameFamilyWidening_SeekPreserved()
    {

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

        var column = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: WindowsCollation);

        Assert.Equal(Verdict.OperandClash, VerdictClassifier.Classify(column, value));
        Assert.Equal(Verdict.OperandClash, VerdictClassifier.Classify(column, value, otherIsLiteral: false));
    }

    [Fact]
    public void Classify_CrossCategoryStringPair_DifferentCollation_OtherNotProvenLiteral_OperandClash()
    {

        var column = new SqlType(SqlTypeCategory.Char, Length: 10, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: WindowsCollation);

        Assert.Equal(Verdict.OperandClash, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_CrossCategoryStringPair_DifferentCollation_OtherIsLiteral_NotOperandClash()
    {

        var column = new SqlType(SqlTypeCategory.Char, Length: 10, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: WindowsCollation);

        Assert.NotEqual(Verdict.OperandClash, VerdictClassifier.Classify(column, value, otherIsLiteral: true));
    }

    [Fact]
    public void Classify_SameCategory_ColumnCollationUnresolved_OtherCollationResolved_Unknown()
    {

        var column = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: null);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: SqlCollation);

        var (verdict, reason) = VerdictClassifier.ClassifyWithReason(column, value, otherIsLiteral: true);

        Assert.Equal(Verdict.Unknown, verdict);
        Assert.Equal("collation-unresolved", reason);
    }

    [Fact]
    public void Classify_SameCategory_ColumnCollationResolved_OtherCollationUnresolved_SeekPreserved()
    {

        var column = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: null);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_CrossCategoryStringPair_SameCollation_NoConflict()
    {

        var column = new SqlType(SqlTypeCategory.Char, Length: 10, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: SqlCollation);

        Assert.NotEqual(Verdict.OperandClash, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_SameCategoryDifferentCollation_OtherIsLiteral_ScanForced()
    {

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

        var column = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 100, Collation: SqlCollation);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_SameCategoryFacetDifference_DecimalDifferingPrecisionAndScale_SeekPreserved()
    {

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

        var column = new SqlType(SqlTypeCategory.DateTime);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 10);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_VarcharColumnVsDateTimeValue_ColumnConverts_ScanForced()
    {

        var column = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.DateTime);

        Assert.Equal(Verdict.ScanForced, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_BitColumnVsIntLiteral_OracleVerifiedNoConversion_SeekPreserved()
    {

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

        var column = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: SqlCollation);
        var value = new SqlType(SqlTypeCategory.Int);

        Assert.Equal(Verdict.ScanForced, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_IntColumnVsVarcharValue_ColumnOutranksValue_SeekPreserved()
    {

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

        var column = new SqlType(SqlTypeCategory.SqlVariant);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 10, Collation: SqlCollation);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_InModelColumnVsSqlVariantValue_HighestPrecedence_ScanForced()
    {

        var column = new SqlType(SqlTypeCategory.Int);
        var value = new SqlType(SqlTypeCategory.SqlVariant);

        Assert.Equal(Verdict.ScanForced, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_SqlVariantColumnVsSqlVariantValue_BothOutOfModel_Unknown()
    {

        var column = new SqlType(SqlTypeCategory.SqlVariant);
        var value = new SqlType(SqlTypeCategory.SqlVariant);

        Assert.Equal(Verdict.Unknown, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_SqlVariantColumnVsXmlValue_BothOutOfModel_Unknown()
    {

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

        var column = new SqlType(SqlTypeCategory.UniqueIdentifier);
        var value = new SqlType(SqlTypeCategory.VarChar, Length: 36);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_VarBinaryColumnVsTimestampValue_OracleVerified_ScanForced()
    {

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

        var column = new SqlType(SqlTypeCategory.Binary, Length: 16);
        var value = new SqlType(SqlTypeCategory.VarBinary, Length: 16);

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }

    [Theory]
    [MemberData(nameof(AllMatrixEntries))]
    public void Classify_NeverDisagreesWithItsOwnOracleProbedMatrix(
        SqlTypeCategory columnCategory, SqlTypeCategory otherCategory, string? collationName, bool columnConverts, bool compileFailed, bool dynamicRangeSeekAvailable)
    {

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

        var column = new SqlType(SqlTypeCategory.VarChar, Length: 50, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"));
        var value = new SqlType(SqlTypeCategory.VarChar, IsMax: true, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"));

        Assert.Equal(Verdict.RangeSeek, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_BoundedColumnVsMaxValue_WindowsCollation_RangeSeek()
    {

        var column = new SqlType(SqlTypeCategory.VarChar, Length: 50, Collation: new Collation("Latin1_General_CI_AS"));
        var value = new SqlType(SqlTypeCategory.VarChar, IsMax: true, Collation: new Collation("Latin1_General_CI_AS"));

        Assert.Equal(Verdict.RangeSeek, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_BoundedColumnVsMaxValue_UnresolvedCollation_StillRangeSeek()
    {

        var column = new SqlType(SqlTypeCategory.VarChar, Length: 50, Collation: null);
        var value = new SqlType(SqlTypeCategory.VarChar, IsMax: true, Collation: null);

        Assert.Equal(Verdict.RangeSeek, VerdictClassifier.Classify(column, value));
    }

    [Fact]
    public void Classify_BothMaxSameCategory_SameCollation_SeekPreserved()
    {

        var column = new SqlType(SqlTypeCategory.VarChar, IsMax: true, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"));
        var value = new SqlType(SqlTypeCategory.VarChar, IsMax: true, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"));

        Assert.Equal(Verdict.SeekPreserved, VerdictClassifier.Classify(column, value));
    }
}
