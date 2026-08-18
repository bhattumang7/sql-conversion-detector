using SilentScan.Core.Diagnostics;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Implemented by every finding record the dynamic-SQL pipeline can relocate from reparsed-text
/// coordinates back to real source coordinates - once directly (<c>Remap</c>, the script's own
/// segment map) and once per additional nesting level (<c>RemapNested</c>, chaining through the
/// enclosing script's segment map). <see cref="PositionColumn"/> exists only because the finding
/// types disagree on what to call their column-position field (<c>Column</c> on some, because
/// <c>ColumnPosition</c> is taken by an unrelated same-named field there; <c>ColumnPosition</c> on
/// the rest) - <see cref="Relocated"/> is where each record's own field gets written back, keeping
/// that naming difference local to the record instead of leaking into every call site.
/// </summary>
internal interface IRelocatableFinding<TSelf>
    where TSelf : IRelocatableFinding<TSelf>
{
    string SourcePath { get; }
    int Line { get; }
    int PositionColumn { get; }
    SourceSpan? DynamicSqlCallSite { get; }
    FindingConfidence Confidence { get; }

    TSelf Relocated(SourceSpan span, SourceSpan? callSite, FindingConfidence confidence);
}
