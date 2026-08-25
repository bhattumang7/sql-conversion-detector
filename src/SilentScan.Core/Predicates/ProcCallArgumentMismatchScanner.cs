using SilentScan.Core.Common;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public static class ProcCallArgumentMismatchScanner
{
    public static IReadOnlyList<ProcCallArgumentMismatchFinding> Scan(ProcCallGraph graph)
    {
        var findings = new List<ProcCallArgumentMismatchFinding>();

        foreach (var edge in graph.Edges)
        {
            foreach (var argument in edge.Arguments)
            {
                AddIfNarrowing(findings, edge, argument);
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

    private static void AddIfNarrowing(List<ProcCallArgumentMismatchFinding> findings, ProcCallEdge edge, ProcCallArgument argument)
    {
        if (argument.CallerVariableWasAssignedBeforeCall
            && argument.CallerArgumentType is { } callerType
            && argument.FormalParameterType is { } formalType
            && WriteLossClassifier.Classify(formalType, callerType, argument.CallerArgumentExpression, isVariableTarget: true) is { } passedInKind)
        {
            findings.Add(new ProcCallArgumentMismatchFinding(
                edge.CallerScopeQualifiedName, edge.CalleeQualifiedName, argument.FormalParameterName,
                DisplayFor(argument), callerType.ToString(), formalType.ToString(), passedInKind, IsOutputWriteback: false,
                edge.CallSite.SourcePath, edge.CallSite.Line, edge.CallSite.Column));
        }

        if (argument.FormalParameterIsOutput && argument.CallSiteHasOutputKeyword
            && argument.CallerVariableName is { } callerVariableName
            && argument.CallerArgumentType is { } outputCallerType
            && argument.FormalParameterType is { } outputFormalType
            && WriteLossClassifier.Classify(outputCallerType, outputFormalType, sourceExpression: null, isVariableTarget: true) is { } passedBackKind)
        {
            findings.Add(new ProcCallArgumentMismatchFinding(
                edge.CallerScopeQualifiedName, edge.CalleeQualifiedName, argument.FormalParameterName,
                callerVariableName, outputCallerType.ToString(), outputFormalType.ToString(), passedBackKind, IsOutputWriteback: true,
                edge.CallSite.SourcePath, edge.CallSite.Line, edge.CallSite.Column));
        }
    }

    private static string DisplayFor(ProcCallArgument argument) =>
        argument.CallerVariableName
        ?? (argument.CallerArgumentExpression is { } expression ? FragmentTextRenderer.Render(expression) : argument.FormalParameterName);
}
