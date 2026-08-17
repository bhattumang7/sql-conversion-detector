namespace SilentScan.Core.Predicates;

public enum TransactionHygieneFindingKind
{
    /// <summary>
    /// A <c>BEGIN TRANSACTION</c> reaches a <c>RETURN</c>/<c>THROW</c> statement, or the natural
    /// end of the module body, on some statically reachable path with no intervening
    /// <c>COMMIT</c>/<c>ROLLBACK</c> at the same nesting level - <c>@@TRANCOUNT</c> is left
    /// elevated by one relative to entry on that path, a real, structural correctness defect
    /// independent of any specific runtime data.
    /// </summary>
    UnresolvedOnSomePath,
}

/// <summary>
/// docs/detection-checklist.md "Small precise adds": the first half of the "Transaction hygiene
/// pair" item - <c>BEGIN TRANSACTION</c> with no reachable <c>ROLLBACK</c>/<c>COMMIT</c> on some
/// path leaves locks held indefinitely. (The second half, "lengthy work between an error and its
/// ROLLBACK," was investigated and NOT built - see docs/detection-checklist.md for why; nothing
/// survived as a genuinely precise, non-magnitude-guessing static claim distinct from this one.)
///
/// A correctness/robustness finding, not a plan-shape one - no plan-XML oracle applies.
/// Oracle-confirmed the underlying mechanism directly (real execution on the Docker instance,
/// <c>@@TRANCOUNT</c> read after calling a procedure matching this exact shape): a procedure whose
/// only path opens a transaction and then RETURNs without resolving it leaves the CALLING
/// session's <c>@@TRANCOUNT</c> elevated by one after the call returns - the same real,
/// unconditional SQL Server behavior regardless of whether the proc itself was called from inside
/// an already-open caller transaction or not (each unmatched <c>BEGIN TRANSACTION</c> increments
/// <c>@@TRANCOUNT</c> by exactly one, and nothing but a matching <c>COMMIT</c>/<c>ROLLBACK</c>
/// decrements it).
///
/// <see cref="TransactionHygieneScanner"/>'s own doc comment states the reachability analysis and
/// its known v1 scope limits precisely - summarized here: only ONE currently-tracked
/// <c>BEGIN TRANSACTION</c> instance is followed at a time (a second, nested
/// <c>BEGIN TRANSACTION</c> found while already tracking one, and any module containing a
/// <c>GOTO</c>, is declined rather than guessed at); a <c>CATCH</c> block is analyzed as entering
/// with whatever transaction state existed at the START of its own <c>TRY</c>/<c>CATCH</c>
/// construct (a conservative approximation, since an error inside <c>TRY</c> could occur at any
/// point, not necessarily before any relevant statement runs) - stated honestly as an
/// approximation, not a guaranteed-exact reconstruction of every possible error timing.
///
/// <see cref="FindingConfidence.High"/>, SARIF Warning - the same "structural risk, not a plan-
/// shape claim" tier <see cref="ForcedSerialFinding"/>/<see cref="WaitForFinding"/> already use.
/// Version-insensitive: <c>@@TRANCOUNT</c> bookkeeping is ANSI/T-SQL session-state semantics,
/// unaffected by compat level or CE mode.
/// </summary>
public sealed record TransactionHygieneFinding(
    TransactionHygieneFindingKind Kind,
    string SourcePath,
    int BeginTransactionLine,
    int BeginTransactionColumn,
    int UnresolvedExitLine,
    int UnresolvedExitColumn,
    FindingConfidence Confidence = FindingConfidence.High);
