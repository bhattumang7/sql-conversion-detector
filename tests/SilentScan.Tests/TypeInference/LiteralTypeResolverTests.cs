using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.TypeInference;

public sealed class LiteralTypeResolverTests
{
    private static Literal ParseLiteral(string expressionSql)
    {
        var parser = new TSql160Parser(true);
        using var reader = new StringReader($"SELECT {expressionSql};");
        var fragment = parser.Parse(reader, out var errors);
        Assert.Empty(errors);

        var script = (TSqlScript)fragment;
        var select = (SelectStatement)script.Batches[0].Statements[0];
        var spec = (QuerySpecification)select.QueryExpression;
        var scalar = (SelectScalarExpression)spec.SelectElements[0];
        return Assert.IsType<Literal>(scalar.Expression, exactMatch: false);
    }

    [Fact]
    public void Resolve_NationalStringLiteral_ResolvesToNVarChar()
    {
        var type = LiteralTypeResolver.Resolve(ParseLiteral("N'hello'"));

        Assert.Equal(SqlTypeCategory.NVarChar, type!.Category);
        Assert.Equal(5, type.Length);
    }

    [Fact]
    public void Resolve_StringLiteral_ResolvesToVarChar()
    {
        var type = LiteralTypeResolver.Resolve(ParseLiteral("'hello'"));

        Assert.Equal(SqlTypeCategory.VarChar, type!.Category);
        Assert.Equal(5, type.Length);
    }

    [Fact]
    public void Resolve_IntegerLiteral_ResolvesToInt()
    {
        var type = LiteralTypeResolver.Resolve(ParseLiteral("123"));

        Assert.Equal(SqlTypeCategory.Int, type!.Category);
    }

    [Fact]
    public void Resolve_DecimalLiteral_ResolvesToDecimalWithPrecisionAndScale()
    {
        var type = LiteralTypeResolver.Resolve(ParseLiteral("1.5"));

        Assert.Equal(SqlTypeCategory.Decimal, type!.Category);
        Assert.Equal(2, type.Precision);
        Assert.Equal(1, type.Scale);
    }

    [Fact]
    public void Resolve_OutOfIntRangeIntegerValuedLiteral_ParsesAsNumericWithZeroScale()
    {

        var type = LiteralTypeResolver.Resolve(ParseLiteral("99999999999999999999"));

        Assert.Equal(SqlTypeCategory.Decimal, type!.Category);
        Assert.Equal(20, type.Precision);
        Assert.Equal(0, type.Scale);
    }

    [Fact]
    public void Resolve_MoneyLiteral_ResolvesToMoney()
    {
        var type = LiteralTypeResolver.Resolve(ParseLiteral("$5.00"));

        Assert.Equal(SqlTypeCategory.Money, type!.Category);
    }

    [Fact]
    public void Resolve_BinaryLiteral_ResolvesToBinaryWithByteLength()
    {
        var type = LiteralTypeResolver.Resolve(ParseLiteral("0x1A2B"));

        Assert.Equal(SqlTypeCategory.Binary, type!.Category);
        Assert.Equal(2, type.Length);
    }

    [Fact]
    public void Resolve_NullLiteral_ReturnsNull()
    {
        var type = LiteralTypeResolver.Resolve(ParseLiteral("NULL"));

        Assert.Null(type);
    }

    [Fact]
    public void Resolve_StringLiteralWithNoCollateClause_HasNullCollation()
    {

        var type = LiteralTypeResolver.Resolve(ParseLiteral("'hello'"));

        Assert.Null(type!.Collation);
    }

    [Fact]
    public void Resolve_StringLiteralWithExplicitCollate_PropagatesCollationOntoType()
    {
        var type = LiteralTypeResolver.Resolve(ParseLiteral("'hello' COLLATE Latin1_General_CI_AS"));

        Assert.Equal("Latin1_General_CI_AS", type!.Collation!.Name);
    }

    [Fact]
    public void Resolve_NationalStringLiteralWithExplicitCollate_PropagatesCollationOntoType()
    {
        var type = LiteralTypeResolver.Resolve(ParseLiteral("N'hello' COLLATE SQL_Latin1_General_CP1_CI_AS"));

        Assert.Equal(SqlTypeCategory.NVarChar, type!.Category);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", type.Collation!.Name);
    }

    [Fact]
    public void Resolve_StringLiteralWithExplicitCollate_OracleVerified_RanksAtCastCoercibility()
    {
        var type = LiteralTypeResolver.Resolve(ParseLiteral("'hello' COLLATE Latin1_General_CI_AS"));

        Assert.Equal(CollationSource.ExplicitCollateClause, type!.Collation!.Source);
    }

    [Fact]
    public void Resolve_EmptyStringLiteral_ResolvesToLengthOneNotZero()
    {

        var type = LiteralTypeResolver.Resolve(ParseLiteral("''"));

        Assert.Equal(SqlTypeCategory.VarChar, type!.Category);
        Assert.Equal(1, type.Length);
    }

    [Fact]
    public void Resolve_EmptyNationalStringLiteral_ResolvesToLengthOneNotZero()
    {
        var type = LiteralTypeResolver.Resolve(ParseLiteral("N''"));

        Assert.Equal(SqlTypeCategory.NVarChar, type!.Category);
        Assert.Equal(1, type.Length);
    }

    [Fact]
    public void Resolve_ScientificNotationLiteral_ResolvesToFloat()
    {

        var type = LiteralTypeResolver.Resolve(ParseLiteral("1.5e10"));

        Assert.Equal(SqlTypeCategory.Float, type!.Category);
    }

    [Fact]
    public void Resolve_NegativeExponentScientificNotationLiteral_ResolvesToFloat()
    {
        var type = LiteralTypeResolver.Resolve(ParseLiteral("1.5E-10"));

        Assert.Equal(SqlTypeCategory.Float, type!.Category);
    }

    [Fact]
    public void Resolve_IntMaxValuePlusOneIntegerValuedLiteral_ResolvesToDecimalNotBigInt()
    {

        var type = LiteralTypeResolver.Resolve(ParseLiteral("2147483648"));

        Assert.Equal(SqlTypeCategory.Decimal, type!.Category);
        Assert.Equal(10, type.Precision);
        Assert.Equal(0, type.Scale);
    }

    [Fact]
    public void Resolve_BigIntMaxValueIntegerValuedLiteral_ResolvesToDecimalNotBigInt()
    {
        var type = LiteralTypeResolver.Resolve(ParseLiteral("9223372036854775807"));

        Assert.Equal(SqlTypeCategory.Decimal, type!.Category);
        Assert.Equal(19, type.Precision);
        Assert.Equal(0, type.Scale);
    }
}
