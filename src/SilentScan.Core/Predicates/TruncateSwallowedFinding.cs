namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second OSS/commercial sweep": TRUNCATE TABLE inside a TRY block
/// with no matching CATCH - **scope corrected from the item's own original framing once the real
/// T-SQL grammar was checked**: a <c>BEGIN TRY</c> with no corresponding <c>BEGIN CATCH</c> at
/// all is a hard PARSE ERROR (Msg 102, oracle-confirmed directly) - TRY and CATCH are paired
/// grammar, never independently optional, so "no matching CATCH" can never actually occur in
/// valid T-SQL. The real, narrower shape: a CATCH block that SWALLOWS the error rather than
/// propagating it - no <c>THROW</c>/<c>RAISERROR</c> anywhere in the CATCH block's own statement
/// tree (including one nested inside an IF/BEGIN), an empty CATCH being the most extreme case.
/// Oracle-confirmed the underlying mechanism directly: <c>TRUNCATE</c> against a table with an
/// enforced FK reference genuinely fails at runtime (Msg 4712), and when that failure lands
/// inside a TRY whose CATCH is empty, execution continues as if the TRUNCATE had succeeded - no
/// error surfaces to the caller at all, confirmed via a real seeded probe (the referenced table's
/// row count is unchanged, and no exception propagates).
///
/// Not verdict-bearing (a correctness/robustness claim, not a plan-shape one) - no plan-XML
/// oracle applies; the underlying swallowed-failure mechanism is confirmed by real execution
/// instead, the same self-authored-probe-row discipline <see cref="WriteLossFinding"/>'s own
/// oracle tests use. <see cref="FindingConfidence.High"/>, SARIF Warning - a real, structural
/// risk (any TRUNCATE inside this shape can fail silently the instant a new FK reference is
/// added, with zero change to the statement itself), not a provably-wrong-result claim for THIS
/// specific execution (whether the TRUNCATE actually fails depends on schema state this pass
/// cannot fully see - a table with no FK references today is still at risk the moment one is
/// added), the same "structural risk" tier <see cref="ForcedSerialFinding"/>/
/// <see cref="CatchAllPredicateFinding"/> already use.
/// </summary>
public sealed record TruncateSwallowedFinding(
    string SourcePath,
    int Line,
    int Column,
    FindingConfidence Confidence = FindingConfidence.High);
