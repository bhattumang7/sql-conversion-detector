namespace SilentScan.Core.Predicates;

public enum SargabilityFindingKind
{
    FunctionWrappedColumn,

    CastOrConvertOnColumn,

    ColumnArithmetic,

    LeadingWildcardLike,

    RegexpPatternNoLiteralPrefix,

    CaseFoldOnColumn,

    DateFunctionOnColumn,

    CharindexOrLeftOnColumn,
}
