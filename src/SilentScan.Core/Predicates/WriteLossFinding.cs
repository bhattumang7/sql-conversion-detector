using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Roadmap Phase E1: an INSERT/UPDATE assignment whose source expression's static type risks
/// silent data loss against its target column - never a seek/scan concern (see
/// <see cref="Rules.Verdict"/> for that), a correctness one.
/// </summary>
public sealed record WriteLossFinding(
    string TableQualifiedName,
    string ColumnName,
    WriteLossKind Kind,
    SqlType TargetType,
    SqlType SourceType,
    string SourcePath,
    int Line,
    int ColumnPosition,
    SourceSpan? DynamicSqlCallSite = null,
    FindingConfidence Confidence = FindingConfidence.High) : IRelocatableFinding<WriteLossFinding>
{
    int IRelocatableFinding<WriteLossFinding>.PositionColumn => ColumnPosition;

    WriteLossFinding IRelocatableFinding<WriteLossFinding>.Relocated(SourceSpan span, SourceSpan? callSite, FindingConfidence confidence) =>
        this with { SourcePath = span.SourcePath, Line = span.Line, ColumnPosition = span.Column, DynamicSqlCallSite = callSite, Confidence = confidence };
}
