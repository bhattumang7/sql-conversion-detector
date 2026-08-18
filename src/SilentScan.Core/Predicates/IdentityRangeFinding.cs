using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum IdentityRangeFindingKind
{
    /// <summary>
    /// An identity column carries a negative seed, or an increment other than 1
    /// (<see cref="Catalog.CatalogColumn.IdentitySeed"/>/<see cref="Catalog.CatalogColumn.IdentityIncrement"/>).
    /// A SCHEMA-decidable fact (docs/detection-checklist.md "DBA-script family sweep" §A's own
    /// three-way design-time-decidability split): identical on a development copy and a production
    /// one, since it is a property of the <c>CREATE TABLE ... IDENTITY(seed, increment)</c>
    /// declaration itself, never live data state. Reported at <see cref="FindingConfidence.Low"/>
    /// and worded informationally rather than as a defect - a negative seed or a non-1 increment is
    /// a real, deliberate design choice in some schemes (a reversed-numbering scheme counting down
    /// from a high seed, an increment of 2 to interleave two writers' own ranges without collision,
    /// a negative increment for a "most recent first" ordering that piggybacks on the clustering
    /// key) at least as often as it is an oversight, so this is a data-modeling signal worth a
    /// second look, not a provable mistake this pass can call one way or the other.
    /// </summary>
    IdentitySeedOrIncrementAnomaly,

    /// <summary>
    /// An identity column's current value (<see cref="Catalog.CatalogColumn.IdentityCurrentValue"/>)
    /// sits within <see cref="Predicates.IdentityRangeScanner.NearExhaustionRemainingFraction"/> of
    /// the column's own declared type's maximum representable value, in the direction the identity
    /// is actually incrementing (<see cref="Catalog.CatalogColumn.IdentityIncrement"/> - a
    /// descending identity is checked against the type's own minimum instead). A DATA-STATE-decidable
    /// fact, not a schema one (docs/detection-checklist.md's own three-way split): meaningful only
    /// against a production-shaped target, where the current value reflects real accumulated
    /// inserts - meaningless against a low-value development database, where an identity sitting at
    /// (say) 400 is not evidence of anything either way. This finding's own <c>DetailText</c> states
    /// that precondition explicitly every time it fires, and <see cref="Predicates.IdentityRangeScanner"/>
    /// never reports a passing/clean state for this half at all (there is no "identity range OK"
    /// finding to report) - the checklist's own explicit instruction: "never report a clean/passing
    /// state as evidence" for a data-state-decidable check, since the absence of a finding on a
    /// low-value dev database proves nothing about production. Once a column's type resolves to a
    /// bound this pass can compute (tinyint/smallint/int/bigint/decimal(p,0) - see <see
    /// cref="Predicates.IdentityRangeScanner"/>'s own doc comment for the full list), running out
    /// means the identity column can no longer accept new rows at all: every subsequent INSERT
    /// raises a hard arithmetic-overflow error (Msg 8115) until the column is widened or reseeded.
    /// </summary>
    IdentityRangeNearExhaustion,
}

/// <summary>
/// docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §A "Identity/sequence range
/// exhaustion" - shipped as its own small type rather than two more <see cref="IndexDesignFindingKind"/>
/// members, because the checklist's own instruction was explicit that this item must be split in
/// two along the design-time-decidability axis, and the two halves genuinely differ in what they
/// even claim: <see cref="IdentityRangeFindingKind.IdentitySeedOrIncrementAnomaly"/> is a SCHEMA
/// fact (same axis as every <see cref="IndexDesignFinding"/> kind), <see
/// cref="IdentityRangeFindingKind.IdentityRangeNearExhaustion"/> is a DATA-STATE fact (the same
/// axis <see cref="Predicates.DatabaseConfigurationFinding"/>'s Query-Store kinds already occupy,
/// but scoped to a single column rather than the whole database) - keeping them on their own type
/// makes that distinction visible in the schema itself (a consumer filtering on finding type sees
/// the split), rather than burying it inside one more <c>IndexDesignFindingKind</c> switch arm a
/// reader has to already know to look for.
///
/// Computed entirely from <see cref="Catalog.CatalogColumn.IsIdentity"/>/<see
/// cref="Catalog.CatalogColumn.IdentitySeed"/>/<see cref="Catalog.CatalogColumn.IdentityIncrement"/>/
/// <see cref="Catalog.CatalogColumn.IdentityCurrentValue"/> - all four populated in the SAME live
/// catalog column read every other column fact already comes from (no separate live round trip),
/// so this stream is live-mode only for the identical structural reason <see
/// cref="IndexDesignFinding"/> is: those fields default to <see langword="null"/> in file mode,
/// which this scanner treats as "unknown, never guess" rather than "zero"/"seed 1, increment 1".
/// Always empty from <see cref="Reporting.ScanReportBuilder"/>, merged in by
/// <c>SilentScan.Live.LiveScanRunner</c> after a real live catalog read - the same pattern
/// <see cref="IndexDesignFinding"/>/<c>TempTableExecShapeFindings</c>/<c>DatabaseConfigurationFindings</c>
/// already established.
///
/// No plan-XML oracle applies to either kind - both are directly-read catalog/data-state facts, not
/// plan-shape or execution-behavior claims. Engine-version insensitive: IDENTITY seed/increment/
/// overflow behavior is long-standing, unchanged storage-engine mechanics.
/// </summary>
public sealed record IdentityRangeFinding(
    IdentityRangeFindingKind Kind,
    string TableQualifiedName,
    string ColumnName,
    string DetailText,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

