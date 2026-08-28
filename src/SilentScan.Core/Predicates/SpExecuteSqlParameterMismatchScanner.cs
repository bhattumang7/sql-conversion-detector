using SilentScan.Core.Common;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public static class SpExecuteSqlParameterMismatchScanner
{
    public static IReadOnlyList<SpExecuteSqlParameterMismatchFinding> Scan(ProcCallGraph graph)
    {
        var findings = new List<SpExecuteSqlParameterMismatchFinding>();

        foreach (var callSite in graph.SpExecuteSqlCallSites)
        {
            foreach (var binding in callSite.Bindings)
            {
                AddIfNarrowing(findings, callSite, binding);
            }
        }

        return
        [
            .. findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    private static void AddIfNarrowing(
        List<SpExecuteSqlParameterMismatchFinding> findings, SpExecuteSqlCallSite callSite, SpExecuteSqlParameterBinding binding)
    {
        if (binding.CallerVariableWasAssignedBeforeCall
            && binding.CallerArgumentType is { } callerType
            && binding.DeclaredType is { } declaredType
            && WriteLossClassifier.Classify(declaredType, callerType, binding.CallerArgumentExpression, isVariableTarget: true) is { } passedInKind)
        {
            findings.Add(new SpExecuteSqlParameterMismatchFinding(
                callSite.CallerScopeQualifiedName, binding.ParameterName, DisplayFor(binding), callerType.ToString(), declaredType.ToString(),
                passedInKind, IsOutputWriteback: false, callSite.CallSite.SourcePath, callSite.CallSite.Line, callSite.CallSite.Column,
                ConfidenceFor(binding)));
        }

        if (binding.DeclaredIsOutput && binding.CallSiteHasOutputKeyword
            && binding.CallerVariableName is { } callerVariableName
            && binding.CallerArgumentType is { } outputCallerType
            && binding.DeclaredType is { } outputDeclaredType
            && WriteLossClassifier.Classify(outputCallerType, outputDeclaredType, sourceExpression: null, isVariableTarget: true) is { } passedBackKind)
        {
            findings.Add(new SpExecuteSqlParameterMismatchFinding(
                callSite.CallerScopeQualifiedName, binding.ParameterName, callerVariableName, outputCallerType.ToString(), outputDeclaredType.ToString(),
                passedBackKind, IsOutputWriteback: true, callSite.CallSite.SourcePath, callSite.CallSite.Line, callSite.CallSite.Column));
        }
    }

    private static string DisplayFor(SpExecuteSqlParameterBinding binding) =>
        binding.CallerVariableName
        ?? (binding.CallerArgumentExpression is { } expression ? FragmentTextRenderer.Render(expression) : binding.ParameterName);

    private static FindingConfidence ConfidenceFor(SpExecuteSqlParameterBinding binding) =>
        binding.CallerFlowApproximate ? FindingConfidence.Medium : FindingConfidence.High;
}
