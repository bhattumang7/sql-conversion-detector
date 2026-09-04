using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Predicates.Normalization;

internal enum CmpOp { Eq, Ne, Lt, Le, Gt, Ge }

internal static class CmpOpHelper
{
    public static CmpOp? ToCmpOp(BooleanComparisonType type) => type switch
    {
        BooleanComparisonType.Equals => CmpOp.Eq,
        BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => CmpOp.Ne,
        BooleanComparisonType.LessThan => CmpOp.Lt,
        BooleanComparisonType.LessThanOrEqualTo or BooleanComparisonType.NotGreaterThan => CmpOp.Le,
        BooleanComparisonType.GreaterThan => CmpOp.Gt,
        BooleanComparisonType.GreaterThanOrEqualTo or BooleanComparisonType.NotLessThan => CmpOp.Ge,
        _ => null,
    };

    public static CmpOp Flip(CmpOp op) => op switch
    {
        CmpOp.Lt => CmpOp.Gt,
        CmpOp.Gt => CmpOp.Lt,
        CmpOp.Le => CmpOp.Ge,
        CmpOp.Ge => CmpOp.Le,
        _ => op,
    };
}
