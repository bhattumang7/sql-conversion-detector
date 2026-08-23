using System.Text.Json.Serialization;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public enum QueryAntiPatternFindingKind
{
    TableVariablePspSkip,

    TableVariableLowCompatEstimate,

    TableVariableStaleEstimateInLoop,

    RbarSingleRowLoopDml,

    GlobalCursorDeclaration,

    CountStarVariableExistenceCheck,

    NonAggregateHavingPredicate,

    UnionOfProvablyDisjointBranches,

    DistinctMaskingJoinFanout,

    UnqualifiedTableReference,

    MergeMissingHoldlock,

    MergeNonUniqueUsingSource,

    MergeUnconditionalDelete,

    RecursiveCteMissingMaxRecursion,

    UnboundedTableWrite,

    LinkedServerOrCrossDatabaseReference,

    MultiRowInsertIgnoreDupKeyDrop,

    AlterTableSwitchColumnMismatch,

    AlterTableSwitchIndexMismatch,

    AlterTableSwitchConstraintMismatch,

    AlterTableSwitchTargetOnlyIndexRestriction,

    AlterTableSwitchFilegroupMismatch,

    AlterTableSwitchTemporalMismatch,

    AlterTableSwitchRuleConstraint,

    AlterTableSwitchCdcPartitionSwitch,

    AlterTableSwitchPartitionFilegroupMismatch,

    AlterTableSwitchFullTextIndexRestriction,
}

public sealed record QueryAntiPatternFinding(
    QueryAntiPatternFindingKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    string DetailText,
    FindingConfidence Confidence = FindingConfidence.Medium)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}
