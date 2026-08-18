using SilentScan.Core.Lineage;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Rules;

/// <summary>
/// Pure decision for which <see cref="ScalarUdfFindingKind"/> a direct scalar-UDF call is,
/// extracted out of <c>ScalarUdfScanner</c>'s visitor (docs/detection-checklist.md "Engineering
/// debt" - separating rule decisions from ScriptDom traversal mechanics). Recognizing which
/// <see cref="ScalarUdfContext"/> the call site sits in stays the caller's own region-tracking
/// concern; this only decides what that already-resolved context means.
/// </summary>
public static class ScalarUdfClassifier
{
    public static ScalarUdfFindingKind ClassifyInvocationKind(ScalarUdfContext context) =>
        context.IsPredicate() ? ScalarUdfFindingKind.PredicateInvocation : ScalarUdfFindingKind.ProjectionInvocation;
}
