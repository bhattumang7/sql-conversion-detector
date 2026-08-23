using SilentScan.Core.Catalog;

namespace SilentScan.Core.Lineage;

public sealed record TvfFenceOrigin(
    string FunctionQualifiedName,
    TableValuedFunctionKind FunctionKind,
    string OriginSourcePath,
    int OriginLine,
    int Depth);
