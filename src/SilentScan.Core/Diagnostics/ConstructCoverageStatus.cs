namespace SilentScan.Core.Diagnostics;

public enum ConstructCoverageStatus
{
    Handled,

    Ledgered,

    Gap,
}

public sealed record ConstructCoverageEntry(
    string Construct,
    string Group,
    ConstructCoverageStatus Status,
    string? VerifiedBy,
    string? Rationale);
