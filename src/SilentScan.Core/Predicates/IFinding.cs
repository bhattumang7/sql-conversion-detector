namespace SilentScan.Core.Predicates;

public interface IFinding
{
    SourceSpan Location { get; }

    FindingConfidence Confidence { get; }
}
