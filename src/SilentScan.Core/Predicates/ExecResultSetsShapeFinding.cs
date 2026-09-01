using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum ExecResultSetsShapeFindingKind
{
    ColumnCountMismatch,

    ColumnTypeMismatch,
}

public sealed record ExecResultSetsShapeFinding(
    ExecResultSetsShapeFindingKind Kind,
    string ExecutedProcQualifiedName,
    int DeclaredColumnCount,
    int DescribedColumnCount,
    string? ColumnName,
    int? ColumnPosition,
    string? DeclaredColumnTypeDisplay,
    string? DescribedColumnTypeDisplay,
    WriteLossKind? WriteLoss,
    string? CallerScopeQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.ExecResultSetsShapeRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}

public sealed record UnanalyzedExecResultSetsSite(
    string ExecutedProcQualifiedName, string Reason, string SourcePath, int Line, int Column);

public sealed record ExecResultSetsShapeReport(
    IReadOnlyList<ExecResultSetsShapeFinding> Findings,
    IReadOnlyList<UnanalyzedExecResultSetsSite> Unanalyzed)
{
    public static readonly ExecResultSetsShapeReport Empty = new([], []);
}
