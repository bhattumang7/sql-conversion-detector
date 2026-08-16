namespace SilentScan.Core.Predicates;

public enum UntrustedConstraintFindingKind
{
    ForeignKey,
    CheckConstraint,
}

/// <summary>
/// docs/detection-checklist.md Tier 2 "Lineage-metric findings" - "Untrusted (WITH NOCHECK) FK/
/// CHECK constraints". A constraint the engine itself doesn't trust
/// (<c>sys.foreign_keys.is_not_trusted</c>/<c>sys.check_constraints.is_not_trusted</c> - almost
/// always the result of a re-enabling <c>ALTER TABLE ... WITH NOCHECK CHECK CONSTRAINT</c>/
/// <c>WITH NOCHECK ADD CONSTRAINT</c> statement, since <c>WITH NOCHECK</c> is the DEFAULT on that
/// re-enabling form, the opposite of the default on the original <c>ADD CONSTRAINT</c>) forfeits
/// the optimizer's join-elimination and constraint-based query rewrites for every query that
/// touches it - a real, structural cost, but not itself a proof of a wrong result (unlike <see
/// cref="NotInNullableSubqueryFinding"/>), so this reports at <see cref="FindingConfidence.High"/>/
/// SARIF Warning, the same "structural risk, not provably-wrong-result" tier
/// <see cref="ForcedSerialFinding"/>/<see cref="SetOptionFinding"/>/<see cref="CatchAllPredicateFinding"/>
/// already use, not the Error tier a correctness finding gets.
///
/// Catalog-only, unconditional - reported once per untrusted, non-disabled constraint, independent
/// of whether any scanned query actually depends on the elimination the optimizer forfeits (the
/// same "reported once per column/object, not once per use site" precedent
/// <see cref="MaxTypedColumnFinding"/> already establishes for a stable schema fact).
///
/// <b>Origin attribution is a known, deliberately deferred gap.</b> The checklist's own note asks
/// to pair this finding with the DDL statement that caused it wherever possible - checked
/// directly: this tool's corpus-deploy pipeline (<c>ScriptDeployer</c>) discards the parsed DDL
/// AST after deployment, with no existing constraint-name-to-(file, line) side-channel anywhere in
/// <c>SilentScan.Core.Corpus</c>/<c>SilentScan.Verify.Deployment</c> to attribute a specific
/// re-enabling statement back to. Building that would be real new cross-project plumbing (deploy-
/// time AST capture threaded into report-construction time), not an incremental add to this
/// stream - deferred rather than half-built. This is also structurally corpus-only even if built:
/// a <c>scan-db</c> target has no deployment script text at all to attribute back to.
/// </summary>
public sealed record UntrustedConstraintFinding(
    UntrustedConstraintFindingKind Kind,
    string ConstraintName,
    string TableQualifiedName,
    string SourcePath,
    int Line,
    FindingConfidence Confidence = FindingConfidence.High);
