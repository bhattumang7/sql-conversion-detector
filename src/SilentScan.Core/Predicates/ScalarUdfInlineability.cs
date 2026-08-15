namespace SilentScan.Core.Predicates;

/// <summary>
/// A finding's own tri-state read of whether its UDF inlines under SQL 2019+ (FROID) - derived
/// from <see cref="Catalog.ScalarUdfInfo.EngineIsInlineable"/> when the live engine reported it,
/// else from <see cref="Catalog.ScalarUdfInfo.InlineabilityBlocker"/>'s presence/absence.
/// <see cref="Inlineable"/> is asserted ONLY from the engine flag - a clean static blocker scan
/// alone (blocker == null, no engine flag) always reports <see cref="Unknown"/>, never
/// <see cref="Inlineable"/>, since the scan's blocker list is deliberately incomplete.
/// </summary>
public enum ScalarUdfInlineability
{
    NotInlineable,
    Inlineable,
    Unknown,
}
