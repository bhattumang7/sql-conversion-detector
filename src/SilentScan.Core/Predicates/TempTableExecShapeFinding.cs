using System.Text.Json.Serialization;


namespace SilentScan.Core.Predicates;

public enum TempTableExecShapeFindingKind
{
    ColumnCountMismatch,

    ColumnTypeMismatch,
}

public sealed record TempTableExecShapeFinding(
    TempTableExecShapeFindingKind Kind,
    string TempTableQualifiedName,
    string ExecutedProcQualifiedName,
    int TempTableDeclaredColumnCount,
    int DescribedColumnCount,
    string? ColumnName,
    int? ColumnPosition,
    string? TempColumnTypeDisplay,
    string? DescribedColumnTypeDisplay,
    WriteLossKind? WriteLoss,
    string? CallerScopeQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

public sealed record UnanalyzedTempTableExecSite(
    string TempTableQualifiedName, string ExecutedProcQualifiedName, string Reason, string SourcePath, int Line, int Column);

public sealed record TempTableExecShapeReport(
    IReadOnlyList<TempTableExecShapeFinding> Findings,
    IReadOnlyList<UnanalyzedTempTableExecSite> Unanalyzed)
{
    public static readonly TempTableExecShapeReport Empty = new([], []);
}
