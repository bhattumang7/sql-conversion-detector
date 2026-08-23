namespace SilentScan.Core.Predicates;

public enum SargabilityFindingKind
{
    FunctionWrappedColumn,

    CastOrConvertOnColumn,

    ColumnArithmetic,

    LeadingWildcardLike,

    LikePatternNotLiteral,

    CaseFoldOnColumn,

    DateFunctionOnColumn,

    CharindexOrLeftOnColumn,
}
