namespace SilentScan.Core.Lineage;

public enum ScalarUdfContext
{
    Where,
    JoinOn,
    Having,
    MergeOn,
    SelectList,
    OrderBy,
    GroupBy,
    SetAssignment,
    VariableAssignment,

    Other,
}
