using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>
/// A table carries two or more enabled AFTER triggers for the SAME firing event (INSERT, UPDATE,
/// or DELETE) with no <c>sp_settriggerorder</c> pin at all narrowing them down to a single
/// unordered pair - <see cref="Catalog.CatalogTriggerEvent"/>'s own doc comment has the oracle
/// confirmation that <c>sys.trigger_events.is_first</c>/<c>is_last</c> report this pin state
/// directly. SQL Server documents that among any triggers NOT pinned First or Last, relative
/// firing order is undefined and the engine reserves the right to pick any order - Microsoft's own
/// guidance is explicit that code must not depend on it. Pinning one trigger First and a different
/// one Last does not close this gap by itself: with three or more triggers on the same event, the
/// remaining ones (everything neither First nor Last) are still unordered relative to EACH OTHER,
/// so this finding fires whenever that unordered "middle" set has two or more members, not merely
/// whenever a pin is missing entirely. A table with exactly two triggers on the same event and
/// BOTH ends pinned (one First, the other Last) has a middle set of zero and is correctly never
/// flagged - order is then fully determined by the two pins alone. <c>INSTEAD OF</c> triggers are
/// excluded: the engine allows at most one INSTEAD OF trigger per table per event, so the ordering
/// question this finding asks can never even arise for them. A disabled trigger never fires at all
/// and is excluded from both the trigger count and the middle-set count. Live-only end to end
/// (<see cref="Catalog.DatabaseCatalog.TriggerEvents"/> is never populated from parsed DDL - a
/// trigger's own <c>sp_settriggerorder</c> pin state has no DDL representation to replay, the same
/// "engine-authoritative only" reasoning <see cref="Catalog.CatalogCheckConstraint"/>'s own doc
/// comment gives). <see cref="FindingConfidence.Medium"/>: the undefined-order fact itself is
/// mechanical and certain, but whether any of the unordered triggers actually depends on running
/// before/after a sibling (versus each being independent, order-agnostic work) is intent this pass
/// cannot see from catalog state alone - matching this codebase's own tier for a real, catalog-
/// provable structural risk that isn't always an active bug (<see
/// cref="QueryAntiPatternFindingKind.UnboundedTableWrite"/>).
/// </summary>
public sealed record TriggerOrderFinding(
    string TableQualifiedName,
    string EventTypeDescription,
    IReadOnlyList<string> UnorderedTriggerNames,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.Medium)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}
