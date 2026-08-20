using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
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
        // Large enough to overflow int/bigint parsing as IntegerLiteral, so ScriptDOM
        // classifies it as NumericLiteral instead - exercises ResolveNumeric's no-dot branch.
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
        // A plain literal carries no collation of its own here - it's T-SQL's "coercible
        // default" tier, always yielding to whatever the other side needs, never forcing a
        // conversion by itself (Rules.VerdictClassifier's otherIsLiteral rule depends on this).
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
    public void Resolve_EmptyStringLiteral_ResolvesToLengthOneNotZero()
    {
        // Oracle-verified (sys.dm_exec_describe_first_result_set): '' types as varchar(1), not
        // varchar(0) - a zero-length string type isn't real T-SQL (docs/audit-remediation-
        // plan.md Phase 5.3, audit finding C4).
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
        // Oracle-verified: 1.5e10 types as float(53), not real and not decimal
        // (docs/audit-remediation-plan.md Phase 5.3, audit finding C4 - this was the one part
        // of C4 that did hold up: RealLiteral previously fell through to null/untyped).
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
        // Oracle-verified (sys.dm_exec_describe_first_result_set against the real engine): this
        // types as decimal(10,0), NOT bigint - contrary to the commonly-cited "int -> bigint ->
        // decimal" precedence folklore the original audit finding assumed. Locks in that the
        // existing no-dot ResolveNumeric branch was already correct.
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
