using SilentScan.Core.Catalog;

namespace SilentScan.Core.Lineage;

public sealed record ScalarUdfOrigin(
    string FunctionQualifiedName,
    ScalarUdfKind UdfKind,
    ScalarUdfContext OriginContext,
    string OriginSourcePath,
    int OriginLine,
    int Depth);

public static class ScalarUdfContextExtensions
{
    public static bool IsPredicate(this ScalarUdfContext context) =>
        context is ScalarUdfContext.Where or ScalarUdfContext.JoinOn or ScalarUdfContext.Having or ScalarUdfContext.MergeOn;
}
