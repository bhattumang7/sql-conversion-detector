namespace SilentScan.Core.Predicates;

/// <summary>
/// One hop of a <see cref="TriggerRecursionCycleFinding"/>'s own cycle: the trigger that fires on
/// <see cref="FromTableQualifiedName"/> and, somewhere in its own body, writes directly to
/// <see cref="ToTableQualifiedName"/> at <see cref="WriteLine"/> - the next table's own trigger (if
/// the cycle continues) is the next hop's <see cref="TriggerQualifiedName"/>.
/// </summary>
public sealed record TriggerRecursionCycleHop(
    string TriggerQualifiedName, string SourcePath, int TriggerLine,
    string FromTableQualifiedName, string ToTableQualifiedName, int WriteLine);

/// <summary>
/// docs/detection-checklist.md "Second full-archive practitioner sweep (2026-08-18)" §G "Multi-hop
/// trigger recursion cycle across tables" - a trigger on table A writes directly to table B, whose
/// own trigger writes directly back to table A (or, more generally, a directed cycle mediated
/// through any number of distinct tables' triggers). Checked directly against the shipped <see
/// cref="TriggerCorrectnessFindingKind.DirectRecursiveTrigger"/>/<c>SelfRecursionCollector</c>
/// before building this: that kind only matches a trigger writing to its OWN target table (a
/// single-node self-loop) - it never walks to a SECOND table's own trigger, so a genuine two-(or
/// more-)table cycle was a real, unbuilt gap, not a duplicate. Built the same way the checklist
/// pointed at: the graph-construction shape of <see cref="ProcCallGraphBuilder"/>/<see
/// cref="ProcCallGraph"/> (collect every edge across the whole scan, since the two triggers forming
/// a cycle routinely live in different files), applied to trigger DML write targets instead of
/// EXEC call sites, then walked for a directed cycle back to the starting table - see <see
/// cref="TriggerRecursionCycleScanner"/>.
///
/// Gating is the sharpest correction this item needed, and it was wrong as first drafted in the
/// checklist entry itself (which assumed the existing <c>DirectRecursiveTrigger</c> gate,
/// <c>RECURSIVE_TRIGGERS</c>, would carry over). Oracle-confirmed directly (Docker instance,
/// disposable scratch database, dropped immediately after) that a DIFFERENT-table trigger chain is
/// controlled by a SEPARATE, SERVER-level option instead: with the database-level
/// <c>RECURSIVE_TRIGGERS</c> left OFF (the engine default, and the value the scratch database
/// actually had the whole time) but the server-level <c>nested triggers</c> configuration option ON
/// (also the engine default), an UPDATE against table A fired table A's trigger, which updated
/// table B and genuinely fired table B's trigger, which updated table A and fired table A's
/// trigger AGAIN - a real, unbounded cascade (observed climbing past 15 round trips before the test
/// harness's own guard capped it), entirely unaffected by <c>RECURSIVE_TRIGGERS</c> the whole time.
/// Flipping the SERVER option off (and rebuilding the tables/triggers fresh, since resetting the
/// probe's own row values is itself an UPDATE that would otherwise contaminate the measurement)
/// showed the real gate: table A's own trigger still fired once from the original top-level
/// statement (that first hop is the statement's own direct trigger firing, always allowed
/// regardless of this option), and that trigger's own write to table B still fired table B's
/// trigger once (also a direct top-level invocation from A's own trigger body) - but table B's
/// write back to table A did NOT cascade into table A's trigger a second time, stopping the chain
/// exactly where the server option said it should. So this finding gates on the new
/// <see cref="Catalog.DatabaseCatalog.IsNestedTriggersEnabled"/> being live-confirmed <c>true</c> -
/// live-mode only, same "never overclaim a risk that is not actually live" discipline as
/// <c>DirectRecursiveTrigger</c>'s own gate, just against the correct property for THIS mechanism.
/// Also oracle-confirmed the checklist's own nesting-ceiling claim is exactly right: forcing the
/// cascade to continue (server option ON, no artificial guard) hit a real, hard runtime error at
/// the documented limit - <c>Msg 217, Level 16: Maximum stored procedure, function, trigger, or
/// view nesting level exceeded (limit 32)</c> - so a genuine cycle detected here is a "provably
/// fails once actually exercised" claim, not a style concern.
///
/// V1 scope: only a DIRECT <c>INSERT</c>/<c>UPDATE</c>/<c>DELETE</c>/<c>MERGE</c> target inside a
/// trigger's own body counts as a hop (never through a view, never through dynamic SQL this pass
/// can't see inside - the same direct-target discipline <see cref="CrossModuleLockOrderScanner"/>
/// already established for its own cross-module write-order pass), and only a real base table (<see
/// cref="Catalog.CatalogTableKind.Table"/>, synonym-resolved). Cycle search is capped at 8 hops -
/// real-world trigger chains this deep are vanishingly rare and an unbounded search over a large
/// corpus's full trigger-write graph is its own cost/precision risk; nothing past that depth is
/// silently miscounted as clean; the cap is a scope-down, stated here rather than left implicit.
/// Self-loops (a single trigger writing back to its own table) are excluded from this stream on
/// purpose - that shape is <c>DirectRecursiveTrigger</c>'s own, already-shipped claim, not
/// duplicated here. <see cref="CycleTableQualifiedNames"/> lists the cycle's own tables in a
/// canonical rotation (starting at the alphabetically-first table, ordinal comparison) so the same
/// real cycle always produces the same finding shape regardless of which table a scan happens to
/// visit first; <see cref="Hops"/> is the matching, canonically-rotated list of trigger write edges
/// that close the cycle.
///
/// <see cref="FindingConfidence.Medium"/>, matching <c>DirectRecursiveTrigger</c>'s own precedent
/// for the identical reason: the cascade mechanism itself is mechanical and oracle-confirmed once
/// the gating option is true, but whether every hop's own write statement is actually reached at
/// runtime (each may sit behind a condition this pass does not evaluate, exactly like the
/// self-recursion case) is real data/control-flow this pass cannot fully resolve.
/// </summary>
public sealed record TriggerRecursionCycleFinding(
    IReadOnlyList<string> CycleTableQualifiedNames,
    IReadOnlyList<TriggerRecursionCycleHop> Hops,
    FindingConfidence Confidence = FindingConfidence.Medium);
