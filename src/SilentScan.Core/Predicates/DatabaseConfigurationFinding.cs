using SilentScan.Core.Rules;
namespace SilentScan.Core.Predicates;

public enum DatabaseConfigurationFindingKind
{
    PageVerifyNotChecksum,

    AutoShrinkOn,

    AutoCloseOn,

    TargetRecoveryTimeUnset,

    QueryStoreNotReadWrite,

    QueryStoreCaptureModeNotAuto,

    AutoCreateStatisticsOff,

    AutoUpdateStatisticsOff,

    CompatibilityLevelBehindEngineDefault,

    SpatialPersistedComputedColumnDisabledOnCompatibilityLevelChange,

    PlanGuideAltersOptimization,
}

public sealed record DatabaseConfigurationFinding(
    DatabaseConfigurationFindingKind Kind,
    string DatabaseName,
    FindingConfidence Confidence = FindingConfidence.High,
    string? AffectedObjectName = null,
    string? Dependency = null,
    int? TargetCompatibilityLevel = null,
    string? PlanGuideScopeType = null,
    string? PlanGuideHints = null) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.DatabaseConfigurationRuleId(Kind);

    public SourceSpan Location => new(DatabaseName, 0, 0);
}

