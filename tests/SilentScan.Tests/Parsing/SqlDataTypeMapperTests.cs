using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Parsing;

public sealed class SqlDataTypeMapperTests
{
    [Theory]
    [InlineData(SqlDataTypeOption.BigInt, SqlTypeCategory.BigInt)]
    [InlineData(SqlDataTypeOption.Int, SqlTypeCategory.Int)]
    [InlineData(SqlDataTypeOption.SmallInt, SqlTypeCategory.SmallInt)]
    [InlineData(SqlDataTypeOption.TinyInt, SqlTypeCategory.TinyInt)]
    [InlineData(SqlDataTypeOption.Bit, SqlTypeCategory.Bit)]
    [InlineData(SqlDataTypeOption.Decimal, SqlTypeCategory.Decimal)]
    [InlineData(SqlDataTypeOption.Numeric, SqlTypeCategory.Decimal)]
    [InlineData(SqlDataTypeOption.Money, SqlTypeCategory.Money)]
    [InlineData(SqlDataTypeOption.SmallMoney, SqlTypeCategory.SmallMoney)]
    [InlineData(SqlDataTypeOption.Float, SqlTypeCategory.Float)]
    [InlineData(SqlDataTypeOption.Real, SqlTypeCategory.Real)]
    [InlineData(SqlDataTypeOption.DateTime, SqlTypeCategory.DateTime)]
    [InlineData(SqlDataTypeOption.SmallDateTime, SqlTypeCategory.SmallDateTime)]
    [InlineData(SqlDataTypeOption.Char, SqlTypeCategory.Char)]
    [InlineData(SqlDataTypeOption.VarChar, SqlTypeCategory.VarChar)]
    [InlineData(SqlDataTypeOption.Text, SqlTypeCategory.Text)]
    [InlineData(SqlDataTypeOption.NChar, SqlTypeCategory.NChar)]
    [InlineData(SqlDataTypeOption.NVarChar, SqlTypeCategory.NVarChar)]
    [InlineData(SqlDataTypeOption.NText, SqlTypeCategory.NText)]
    [InlineData(SqlDataTypeOption.Binary, SqlTypeCategory.Binary)]
    [InlineData(SqlDataTypeOption.VarBinary, SqlTypeCategory.VarBinary)]
    [InlineData(SqlDataTypeOption.Image, SqlTypeCategory.Image)]
    [InlineData(SqlDataTypeOption.Sql_Variant, SqlTypeCategory.SqlVariant)]
    [InlineData(SqlDataTypeOption.Timestamp, SqlTypeCategory.Timestamp)]
    [InlineData(SqlDataTypeOption.Rowversion, SqlTypeCategory.Timestamp)]
    [InlineData(SqlDataTypeOption.UniqueIdentifier, SqlTypeCategory.UniqueIdentifier)]
    [InlineData(SqlDataTypeOption.Date, SqlTypeCategory.Date)]
    [InlineData(SqlDataTypeOption.Time, SqlTypeCategory.Time)]
    [InlineData(SqlDataTypeOption.DateTime2, SqlTypeCategory.DateTime2)]
    [InlineData(SqlDataTypeOption.DateTimeOffset, SqlTypeCategory.DateTimeOffset)]
    [InlineData(SqlDataTypeOption.Json, SqlTypeCategory.Json)]
    public void Map_KnownOption_ReturnsExpectedCategory(SqlDataTypeOption option, SqlTypeCategory expected)
    {
        Assert.Equal(expected, SqlDataTypeMapper.Map(option));
    }

    [Theory]
    [InlineData(SqlDataTypeOption.None)]
    [InlineData(SqlDataTypeOption.Cursor)]
    [InlineData(SqlDataTypeOption.Table)]
    public void Map_OutOfScopeOption_ReturnsNull(SqlDataTypeOption option)
    {
        Assert.Null(SqlDataTypeMapper.Map(option));
    }
}
