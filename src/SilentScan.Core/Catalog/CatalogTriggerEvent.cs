namespace SilentScan.Core.Catalog;

/// <summary>
/// One (trigger, firing event) pair, read live from <c>sys.triggers</c>/<c>sys.trigger_events</c> -
/// engine-authoritative by construction, the same reasoning <see cref="CatalogCheckConstraint"/>'s
/// own doc comment gives: a trigger's own <c>sp_settriggerorder</c> pin state has no DDL
/// representation to replay from parsed source at all, it is pure server-side catalog state set by
/// a separate statement that may never appear in the same script (or even the same deployment) as
/// the <c>CREATE TRIGGER</c> itself. <paramref name="IsFirst"/>/<paramref name="IsLast"/> are
/// <c>sys.trigger_events.is_first</c>/<c>is_last</c> directly - oracle-confirmed (disposable Docker
/// scratch database) against <c>sp_settriggerorder</c>: both start false for every trigger on a
/// table+event with no pin set at all, and setting one trigger First (or a different one Last)
/// flips only that trigger's own flag, every sibling trigger on the same event staying false.
/// Always empty for a file-mode scan - matching <see cref="DatabaseCatalog.ForeignKeys"/>'s own
/// "live-only, never inferred from DDL" discipline.
/// </summary>
public sealed record CatalogTriggerEvent(
    string TriggerQualifiedName,
    string TableQualifiedName,
    string EventTypeDescription,
    bool IsInsteadOf,
    bool IsDisabled,
    bool IsFirst,
    bool IsLast,
    string SourcePath,
    int SourceLine);
