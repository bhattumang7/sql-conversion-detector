using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Small precise adds": WAITFOR DELAY/WAITFOR TIME inside a routine or
/// batch. Syntax-only, no catalog dependency and no oracle needed - a worker thread blocked on
/// WAITFOR holds that worker (and, inside a transaction, any locks it holds) idle for the full
/// delay/until-time, a documented, unconditional SQL Server mechanism, not a plan-shape claim.
/// <c>WAITFOR RECEIVE</c>/<c>WAITFOR (Receive statement)</c> (Service Broker) is a distinct,
/// legitimate blocking-wait idiom with its own <c>, TIMEOUT</c> option and is deliberately never
/// matched here - only the DELAY/TIME timer forms, which exist purely to sleep the calling session
/// and have no messaging semantics to justify the wait.
///
/// Both DELAY (relative) and TIME (absolute) share the identical risk - a worker thread held idle,
/// contributing to worker-pool exhaustion under load, and (worse, inside an open transaction) lock
/// hold duration - so both report under one <see cref="FindingConfidence.High"/>, SARIF Warning
/// finding rather than being split into separate Kind values with no differing story to tell.
/// Reported at Warning, the same "structural risk, not provably-wrong-result" tier
/// <see cref="ForcedSerialFinding"/>/<see cref="CatchAllPredicateFinding"/> already use - a real
/// cost, not a correctness claim, and one this pass cannot itself size (how long, how often, is
/// data this static pass cannot see).
/// </summary>
public sealed record WaitForFinding(
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    bool IsInsideTransaction,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

