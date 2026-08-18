using System.Text.Json.Serialization;
using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "Lineage-metric findings" - "Cascading FK actions (ON
/// DELETE/UPDATE CASCADE)". A foreign key whose <c>delete_referential_action</c>/
/// <c>update_referential_action</c> is not <see cref="ReferentialAction.NoAction"/> makes a single
/// DML statement against the referenced (parent) table silently touch every dependent row in the
/// child table too - real, hidden multi-table work with no visible predicate change at the call
/// site, the same "invisible at the call site" framing <see cref="SetOptionFinding"/> uses.
///
/// Purely informational, catalog-only, unconditional - reported once per cascading FK,
/// independent of whether any scanned DML actually deletes/updates the parent (a stable schema
/// fact, matching <see cref="MaxTypedColumnFinding"/>'s own "reported once per object" precedent).
/// <see cref="FindingConfidence.High"/> - the action is an exact catalog fact, not an inference -
/// but this makes no claim about MAGNITUDE (how many rows, how often) the way
/// <see cref="LocalVariablePredicateFinding"/>'s own doc comment draws the same distinction for
/// its own no-magnitude-claim tier: reported at SARIF Note, not Warning/Error, since knowing a
/// cascade exists is useful awareness, not itself a proven cost.
/// </summary>
public sealed record CascadingForeignKeyFinding(
    string ConstraintName,
    string ParentTableQualifiedName,
    string ReferencedTableQualifiedName,
    ReferentialAction DeleteAction,
    ReferentialAction UpdateAction,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

