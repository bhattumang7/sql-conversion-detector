using System.Text.Json.Serialization;
using SilentScan.Core.Common;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum DuplicationFindingKind
{
    CommentedOutCode,

    DuplicatedStringLiteral,

    SingleIterationLoop,

    SelfAssignment,

    IdenticalBinaryOperands,

    RepeatedUnaryOperator,

    NegatedComparisonAsOpposite,

    DuplicateSiblingCondition,

    IdenticalBranchBodies,

    AllBranchesIdentical,

    RedundantAndCondition,

    MutuallyExclusiveAndCondition,

    CollapsibleNestedIf,

    NestedConditionalExpression,

    AlwaysTrueOrFalseLiteralComparison,
}

public sealed record DuplicationFinding(
    DuplicationFindingKind Kind,
    string ModuleQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    string? DetailText = null,
    FindingConfidence Confidence = FindingConfidence.Medium) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.DuplicationRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}

