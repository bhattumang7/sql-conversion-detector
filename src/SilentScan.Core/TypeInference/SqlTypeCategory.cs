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

    /// <summary>
    /// Not part of the official precedence list (too new - SQL Server 2025) and not comparable at
    /// all: oracle-confirmed <c>json = json</c>, <c>json = '{}'</c>, and even <c>json = NULL</c>
    /// (as opposed to <c>IS NULL</c>) all raise Msg 13636 "The JSON data type cannot be compared
    /// or sorted, except when using the IS NULL operator" - stricter than <see cref="Xml"/>, which
    /// this sits next to in <c>VerdictClassifier.IsOutOfModelCategory</c>.
    /// </summary>
    Json,
}
#pragma warning restore CA1720
