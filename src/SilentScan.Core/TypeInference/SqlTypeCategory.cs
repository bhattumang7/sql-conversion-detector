namespace SilentScan.Core.TypeInference;

#pragma warning disable CA1720
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

    Json,
    HierarchyId,
    Geometry,
    Geography,
    Vector,
}
#pragma warning restore CA1720
