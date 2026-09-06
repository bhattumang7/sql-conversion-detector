namespace SilentScan.Core.Predicates;

public enum SargabilityFindingKind
{
    FunctionWrappedColumn,

    CastOrConvertOnColumn,

    ColumnArithmetic,

    LeadingWildcardLike,

    LikePatternNotLiteral,

    RegexpPatternNotLiteral,

    CaseFoldOnColumn,

    DateFunctionOnColumn,

    CharindexOrLeftOnColumn,
}
