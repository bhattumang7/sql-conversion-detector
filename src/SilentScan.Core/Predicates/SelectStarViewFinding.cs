namespace SilentScan.Core.Predicates;

public sealed record SelectStarViewFinding(
    string ViewQualifiedName,
    string ViewSourcePath,
    int ViewLine,
    IReadOnlyList<string> ViewFullColumns,
    int ViewDepth,
    string ConsumerSourcePath,
    int ConsumerLine,
    int ConsumerColumn,
    IReadOnlyList<string> ConsumerSelectedColumns,
    FindingConfidence Confidence = FindingConfidence.High);
