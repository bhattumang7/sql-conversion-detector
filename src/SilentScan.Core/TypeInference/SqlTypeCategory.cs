namespace SilentScan.Core.TypeInference;

/// <summary>
/// The base T-SQL data type categories, ordered by the official data type precedence
/// list (https://learn.microsoft.com/sql/t-sql/data-types/data-type-precedence-transact-sql).
/// Enum ordinal is precedence rank: HIGHER ordinal = HIGHER precedence = the OTHER side
/// converts. When comparing two operands, the LOWER-precedence side is the one implicitly
/// converted by the engine.
/// </summary>
#pragma warning disable CA1720 // Names deliberately mirror T-SQL keywords (char, int, decimal, float), not CLR types.
public enum SqlTypeCategory
{
    Binary = 0,
    VarBinary,
    Char,
    VarChar,
    NChar,
    NVarChar,
    UniqueIdentifier,
    Timestamp,
    Image,
    Text,
    NText,
    Bit,
    TinyInt,
    SmallInt,
    Int,
    BigInt,
    SmallMoney,
    Money,
    Decimal,
    Real,
    Float,
    Time,
    Date,
    SmallDateTime,
    DateTime,
    DateTime2,
    DateTimeOffset,
    Xml,
    SqlVariant,
    UserDefined,
}
#pragma warning restore CA1720
