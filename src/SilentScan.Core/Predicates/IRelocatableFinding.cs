using SilentScan.Core.Diagnostics;

namespace SilentScan.Core.Predicates;

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
