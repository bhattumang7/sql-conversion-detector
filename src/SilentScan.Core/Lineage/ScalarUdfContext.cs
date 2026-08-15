namespace SilentScan.Core.Lineage;

/// <summary>
/// The exact clause a scalar-UDF call was found in. Lives in Lineage (not Predicates, where the
/// finding stream itself lives) because <see cref="ScalarUdfOrigin"/> needs it too - a nested
/// finding reports the context AT THE INTRODUCING LAYER, which only <see cref="ScalarUdfMap"/>
/// knows, so the same enum has to be shared rather than duplicated as a coarser Lineage-only
/// version and a richer Predicates-only one.
/// </summary>
public enum ScalarUdfContext
{
    Where,
    JoinOn,
    Having,
    MergeOn,
    SelectList,
    OrderBy,
    GroupBy,
    SetAssignment,
    VariableAssignment,

    /// <summary>Any other scalar-expression position - honest fallback, never silently mis-bucketed into one of the named contexts above.</summary>
    Other,
}
