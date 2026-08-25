using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Rules;

public sealed class WriteLossClassifierTests
{
    [Theory]
    [InlineData(SqlTypeCategory.Money, SqlTypeCategory.Decimal, WriteLossKind.NumericScaleNarrowing)]
    [InlineData(SqlTypeCategory.Decimal, SqlTypeCategory.Money, WriteLossKind.NumericScaleNarrowing)]
    [InlineData(SqlTypeCategory.SmallMoney, SqlTypeCategory.Money, null)]
    [InlineData(SqlTypeCategory.Int, SqlTypeCategory.Money, WriteLossKind.NumericScaleNarrowing)]
    public void NumericFamily_MoneyPairs_ClassifiedGenerically(SqlTypeCategory targetCategory, SqlTypeCategory sourceCategory, WriteLossKind? expected)
    {
        var target = new SqlType(targetCategory, Precision: targetCategory == SqlTypeCategory.Decimal ? 10 : null, Scale: targetCategory == SqlTypeCategory.Decimal ? 2 : null);
        var source = new SqlType(sourceCategory, Precision: sourceCategory == SqlTypeCategory.Decimal ? 10 : null, Scale: sourceCategory == SqlTypeCategory.Decimal ? 6 : null);

        var kind = WriteLossClassifier.Classify(target, source, sourceExpression: null, isVariableTarget: true);

        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData(SqlTypeCategory.Decimal, WriteLossKind.ApproximateToExactTruncation)]
    [InlineData(SqlTypeCategory.Money, WriteLossKind.ApproximateToExactTruncation)]
    [InlineData(SqlTypeCategory.Int, WriteLossKind.ApproximateToExactTruncation)]
    public void FloatSource_NarrowsIntoExactNumericTarget_NotJustIntegers(SqlTypeCategory targetCategory, WriteLossKind expected)
    {
        var target = new SqlType(targetCategory, Precision: targetCategory == SqlTypeCategory.Decimal ? 10 : null, Scale: targetCategory == SqlTypeCategory.Decimal ? 2 : null);
        var source = new SqlType(SqlTypeCategory.Float);

        var kind = WriteLossClassifier.Classify(target, source, sourceExpression: null, isVariableTarget: true);

        Assert.Equal(expected, kind);
    }

    [Fact]
    public void FloatSource_IntoNarrowerRealTarget_Flags()
    {
        var target = new SqlType(SqlTypeCategory.Real);
        var source = new SqlType(SqlTypeCategory.Float);

        var kind = WriteLossClassifier.Classify(target, source, sourceExpression: null, isVariableTarget: true);

        Assert.Equal(WriteLossKind.NumericScaleNarrowing, kind);
    }

    [Fact]
    public void RealSource_IntoFloatTarget_NeverFlags()
    {
        var target = new SqlType(SqlTypeCategory.Float);
        var source = new SqlType(SqlTypeCategory.Real);

        var kind = WriteLossClassifier.Classify(target, source, sourceExpression: null, isVariableTarget: true);

        Assert.Null(kind);
    }

    [Fact]
    public void FloatSource_IntoFloatTarget_NeverFlags()
    {
        var target = new SqlType(SqlTypeCategory.Float);
        var source = new SqlType(SqlTypeCategory.Float);

        var kind = WriteLossClassifier.Classify(target, source, sourceExpression: null, isVariableTarget: true);

        Assert.Null(kind);
    }

    [Fact]
    public void Length_NarrowerVarcharTarget_VariableTarget_Flags()
    {
        var target = new SqlType(SqlTypeCategory.VarChar, Length: 3);
        var source = new SqlType(SqlTypeCategory.VarChar, Length: 10);

        var kind = WriteLossClassifier.Classify(target, source, sourceExpression: null, isVariableTarget: true);

        Assert.Equal(WriteLossKind.LengthTruncation, kind);
    }

    [Fact]
    public void Length_NarrowerVarcharTarget_TableColumnTarget_NeverFlags()
    {
        var target = new SqlType(SqlTypeCategory.VarChar, Length: 3);
        var source = new SqlType(SqlTypeCategory.VarChar, Length: 10);

        var kind = WriteLossClassifier.Classify(target, source, sourceExpression: null, isVariableTarget: false);

        Assert.Null(kind);
    }

    [Fact]
    public void Length_NarrowerVarbinaryTarget_VariableTarget_Flags()
    {
        var target = new SqlType(SqlTypeCategory.VarBinary, Length: 4);
        var source = new SqlType(SqlTypeCategory.VarBinary, Length: 16);

        var kind = WriteLossClassifier.Classify(target, source, sourceExpression: null, isVariableTarget: true);

        Assert.Equal(WriteLossKind.LengthTruncation, kind);
    }

    [Fact]
    public void Length_MaxTarget_NeverFlags()
    {
        var target = new SqlType(SqlTypeCategory.VarChar, IsMax: true);
        var source = new SqlType(SqlTypeCategory.VarChar, Length: 10);

        var kind = WriteLossClassifier.Classify(target, source, sourceExpression: null, isVariableTarget: true);

        Assert.Null(kind);
    }

    [Fact]
    public void Length_UnknownSourceLength_NeverGuesses()
    {
        var target = new SqlType(SqlTypeCategory.VarChar, Length: 3);
        var source = new SqlType(SqlTypeCategory.VarChar, Length: null);

        var kind = WriteLossClassifier.Classify(target, source, sourceExpression: null, isVariableTarget: true);

        Assert.Null(kind);
    }

    [Fact]
    public void TemporalScaleNarrowing_DateTime2ToNarrowerDateTime2_Flags()
    {
        var target = new SqlType(SqlTypeCategory.DateTime2, Scale: 2);
        var source = new SqlType(SqlTypeCategory.DateTime2, Scale: 7);

        var kind = WriteLossClassifier.Classify(target, source, sourceExpression: null, isVariableTarget: true);

        Assert.Equal(WriteLossKind.TemporalScaleNarrowing, kind);
    }

    [Fact]
    public void TemporalScaleNarrowing_DateTime2ToTime_Flags()
    {
        var target = new SqlType(SqlTypeCategory.Time, Scale: 0);
        var source = new SqlType(SqlTypeCategory.DateTime2, Scale: 7);

        var kind = WriteLossClassifier.Classify(target, source, sourceExpression: null, isVariableTarget: true);

        Assert.Equal(WriteLossKind.TemporalScaleNarrowing, kind);
    }

    [Fact]
    public void TemporalOffsetDropped_DateTimeOffsetIntoDateTime2_Flags()
    {
        var target = new SqlType(SqlTypeCategory.DateTime2, Scale: 3);
        var source = new SqlType(SqlTypeCategory.DateTimeOffset, Scale: 3);

        var kind = WriteLossClassifier.Classify(target, source, sourceExpression: null, isVariableTarget: true);

        Assert.Equal(WriteLossKind.TemporalOffsetDropped, kind);
    }

    [Fact]
    public void TemporalOffsetDropped_TakesPriorityOverScaleNarrowing_ForSameArgument()
    {
        var target = new SqlType(SqlTypeCategory.DateTime2, Scale: 2);
        var source = new SqlType(SqlTypeCategory.DateTimeOffset, Scale: 7);

        var kind = WriteLossClassifier.Classify(target, source, sourceExpression: null, isVariableTarget: true);

        Assert.Equal(WriteLossKind.TemporalOffsetDropped, kind);
    }

    [Fact]
    public void TemporalOffsetDropped_IntoDateTimeOffsetTarget_NeverFlags()
    {
        var target = new SqlType(SqlTypeCategory.DateTimeOffset, Scale: 3);
        var source = new SqlType(SqlTypeCategory.DateTimeOffset, Scale: 3);

        var kind = WriteLossClassifier.Classify(target, source, sourceExpression: null, isVariableTarget: true);

        Assert.Null(kind);
    }

    [Fact]
    public void Bit_IsNotPulledIntoNumericScaleNarrowing()
    {
        var target = new SqlType(SqlTypeCategory.Bit);
        var source = new SqlType(SqlTypeCategory.Int);

        var kind = WriteLossClassifier.Classify(target, source, sourceExpression: null, isVariableTarget: true);

        Assert.Null(kind);
    }
}
