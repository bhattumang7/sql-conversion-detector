using SilentScan.Core.Lineage;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Rules;

public static class ScalarUdfClassifier
{
    public static ScalarUdfFindingKind ClassifyInvocationKind(ScalarUdfContext context) =>
        context.IsPredicate() ? ScalarUdfFindingKind.PredicateInvocation : ScalarUdfFindingKind.ProjectionInvocation;
}
