using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.TypeInference;

public sealed class SqlTypeCategoryPrecedenceTests
{
    private static readonly SqlTypeCategory[] DocumentedPrecedenceLowestToHighest =
    [
        SqlTypeCategory.Binary,
        SqlTypeCategory.VarBinary,
        SqlTypeCategory.Char,
        SqlTypeCategory.VarChar,
        SqlTypeCategory.NChar,
        SqlTypeCategory.NVarChar,
        SqlTypeCategory.UniqueIdentifier,
        SqlTypeCategory.Timestamp,
        SqlTypeCategory.Image,
        SqlTypeCategory.Text,
        SqlTypeCategory.NText,
        SqlTypeCategory.Bit,
        SqlTypeCategory.TinyInt,
        SqlTypeCategory.SmallInt,
        SqlTypeCategory.Int,
        SqlTypeCategory.BigInt,
        SqlTypeCategory.SmallMoney,
        SqlTypeCategory.Money,
        SqlTypeCategory.Decimal,
        SqlTypeCategory.Real,
        SqlTypeCategory.Float,
        SqlTypeCategory.Time,
        SqlTypeCategory.Date,
        SqlTypeCategory.SmallDateTime,
        SqlTypeCategory.DateTime,
        SqlTypeCategory.DateTime2,
        SqlTypeCategory.DateTimeOffset,
        SqlTypeCategory.Xml,
        SqlTypeCategory.SqlVariant,
        SqlTypeCategory.UserDefined,
    ];

    [Fact]
    public void OracleVerified_EnumOrdinalOrder_MatchesSqlServerDocumentedDataTypePrecedence()
    {
        for (var i = 1; i < DocumentedPrecedenceLowestToHighest.Length; i++)
        {
            var lower = DocumentedPrecedenceLowestToHighest[i - 1];
            var higher = DocumentedPrecedenceLowestToHighest[i];

            Assert.True(
                (int)lower < (int)higher,
                $"{lower} (={(int)lower}) must have a lower ordinal than {higher} (={(int)higher}) - SqlTypeCategory's declaration order encodes SQL Server's data type precedence table, and ExpressionTypeInferencer.Combine relies on it via ordinal comparison.");
        }
    }

    [Fact]
    public void Json_HasTheHighestOrdinalOfAllCategories()
    {
        Assert.All(DocumentedPrecedenceLowestToHighest, other => Assert.True((int)other < (int)SqlTypeCategory.Json));
    }

    [Theory]
    [InlineData(SqlTypeCategory.Binary, SqlTypeCategory.VarBinary)]
    [InlineData(SqlTypeCategory.VarBinary, SqlTypeCategory.Char)]
    [InlineData(SqlTypeCategory.Char, SqlTypeCategory.VarChar)]
    [InlineData(SqlTypeCategory.VarChar, SqlTypeCategory.NChar)]
    [InlineData(SqlTypeCategory.NChar, SqlTypeCategory.NVarChar)]
    public void OracleVerified_SixMemberStringBinaryGroup_MatchesEnumDeclarationOrder(SqlTypeCategory lower, SqlTypeCategory higher)
    {
        Assert.True((int)lower < (int)higher);
    }
}
