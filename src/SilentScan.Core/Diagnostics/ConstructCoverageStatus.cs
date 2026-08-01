namespace SilentScan.Core.Diagnostics;

/// <summary>
/// Where a T-SQL construct stands relative to the analysis passes, as tracked by
/// <see cref="ConstructCoverageCatalog"/>.
/// </summary>
public enum ConstructCoverageStatus
{
    /// <summary>Resolved correctly and contributes to verdicts/lineage as designed.</summary>
    Handled,

    /// <summary>Not resolved, but every occurrence reaches a <see cref="SkipLedger"/> entry - counted, never silently dropped.</summary>
    Ledgered,

    /// <summary>Neither resolved nor consistently ledgered - a real coverage hole.</summary>
    Gap,
}

/// <summary>One row of the construct coverage matrix (docs/coverage-remediation-plan.md Phase 0.1).</summary>
public sealed record ConstructCoverageEntry(
    string Construct,
    string Group,
    ConstructCoverageStatus Status,
    string? VerifiedBy,
    string? Rationale);
