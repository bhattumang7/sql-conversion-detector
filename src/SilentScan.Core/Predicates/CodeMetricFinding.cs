namespace SilentScan.Core.Predicates;

public enum CodeMetricFindingKind
{
    /// <summary>A physical source line exceeds the configured maximum character length.</summary>
    LineTooLong,

    /// <summary>A module (or, in file-mode, a source file) exceeds the configured maximum line count.</summary>
    ModuleTooLong,

    /// <summary>A procedure/function/trigger body exceeds the configured maximum line count.</summary>
    RoutineTooLong,

    /// <summary>A procedure/function declares more formal parameters than the configured maximum.</summary>
    TooManyParameters,

    /// <summary>An IF/WHILE/TRY nests more than the configured maximum depth inside a routine.</summary>
    NestingTooDeep,

    /// <summary>A single IF/WHILE condition chains more AND/OR operators than the configured maximum.</summary>
    TooManyConditionalOperators,

    /// <summary>A single CASE expression has more WHEN branches than the configured maximum.</summary>
    TooManyCaseBranches,

    /// <summary>A single CASE WHEN branch's result expression spans more lines than the configured maximum.</summary>
    CaseBranchTooLong,
}

/// <summary>
/// docs/detection-checklist.md Tier 4 "Size and complexity metrics" - eight configurable-threshold
/// structural metrics over the AST, no catalog needed. Purely a maintainability/readability signal:
/// none of these change a query's result or its plan, so every member is <see
/// cref="FindingConfidence.Low"/> (a real, measured structural fact, but no magnitude/cost claim -
/// the same "true but no cost story" tier <see cref="LocalVariablePredicateFinding"/>/<see
/// cref="CascadingForeignKeyFinding"/> already use), and no oracle applies to any of them (a line
/// count or nesting depth is directly observable from the parse, not a plan-shape or runtime
/// behavior needing engine confirmation - the same reasoning <see cref="MaxTypedColumnFinding"/>
/// already established for a pure catalog/AST fact).
///
/// Every threshold is a constructor parameter with a default calibrated against the real local
/// corpus's own measured distribution (see docs/detection-checklist.md for the real numbers) -
/// never an arbitrarily invented cutoff.
/// </summary>
public sealed record CodeMetricFinding(
    CodeMetricFindingKind Kind,
    string ModuleQualifiedName,
    string SourcePath,
    int Line,
    int Column,
    int MeasuredValue,
    int Threshold,
    string? DetailText = null,
    FindingConfidence Confidence = FindingConfidence.Low);
