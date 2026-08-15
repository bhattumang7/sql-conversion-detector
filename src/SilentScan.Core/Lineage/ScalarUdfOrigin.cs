using SilentScan.Core.Catalog;

namespace SilentScan.Core.Lineage;

/// <summary>
/// What a view or inline TVF inherits from a scalar UDF called somewhere inside its own
/// definition (directly, or through however many further view/iTVF layers) - the 603-iTVF
/// headline case (docs/detection-checklist.md Tier 1 #1): expansion spreads a scalar UDF's
/// per-row/serial cost into every caller, exactly the way <see cref="TvfFenceOrigin"/> spreads a
/// materialization fence.
/// </summary>
/// <param name="FunctionQualifiedName">The scalar UDF actually being called.</param>
/// <param name="UdfKind">T-SQL vs CLR, from <see cref="ScalarUdfInfo.Kind"/>.</param>
/// <param name="OriginContext">The exact clause the introducing call was found in - "worst wins" (see <see cref="ScalarUdfContextExtensions.IsPredicate"/>) when a body carries more than one.</param>
/// <param name="OriginSourcePath">Where the introducing call is actually written - the innermost view/iTVF whose own body calls the UDF directly, fixed regardless of how many further layers this origin later propagates through.</param>
/// <param name="OriginLine">1-based line within <paramref name="OriginSourcePath"/>.</param>
/// <param name="Depth">Layers between a caller of the view this origin is attached to and the call: 1 if that view's own body calls the UDF directly, N+1 if it inherits from a view it references at depth N.</param>
public sealed record ScalarUdfOrigin(
    string FunctionQualifiedName,
    ScalarUdfKind UdfKind,
    ScalarUdfContext OriginContext,
    string OriginSourcePath,
    int OriginLine,
    int Depth);

/// <summary>Which <see cref="ScalarUdfContext"/> values count as a predicate for "worst wins" purposes - shared by <see cref="ScalarUdfMap"/> (choosing one origin per carrier) and <see cref="Predicates.ScalarUdfScanner"/> (choosing a finding's Kind).</summary>
public static class ScalarUdfContextExtensions
{
    public static bool IsPredicate(this ScalarUdfContext context) =>
        context is ScalarUdfContext.Where or ScalarUdfContext.JoinOn or ScalarUdfContext.Having or ScalarUdfContext.MergeOn;
}
