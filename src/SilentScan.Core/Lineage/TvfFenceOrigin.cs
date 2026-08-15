using SilentScan.Core.Catalog;

namespace SilentScan.Core.Lineage;

/// <summary>
/// What a view or inline TVF inherits from a multi-statement/CLR TVF fenced somewhere inside
/// its own definition (directly, or through however many further view/TVF layers) - the
/// "permissions function wrapped in a view" shape (docs/detection-checklist.md Tier 1 #2).
/// </summary>
/// <param name="FunctionQualifiedName">The fencing multi-statement/CLR TVF itself.</param>
/// <param name="FunctionKind">Always <see cref="TableValuedFunctionKind.MultiStatement"/> or <see cref="TableValuedFunctionKind.Clr"/> - never <see cref="TableValuedFunctionKind.Inline"/>.</param>
/// <param name="OriginSourcePath">Where the fencing reference is actually written - the innermost view/iTVF whose own body names the function directly, fixed at the introduction point regardless of how many further layers this origin later propagates through.</param>
/// <param name="OriginLine">1-based line within <paramref name="OriginSourcePath"/>.</param>
/// <param name="Depth">Layers between a caller of the view this origin is attached to and the fence: 1 if that view's own body names the function directly, N+1 if it inherits the fence from a view it references at depth N.</param>
public sealed record TvfFenceOrigin(
    string FunctionQualifiedName,
    TableValuedFunctionKind FunctionKind,
    string OriginSourcePath,
    int OriginLine,
    int Depth);
