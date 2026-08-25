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
                if (argument.CallerVariableName is not { } callerVariableName
                    || argument.CallerArgumentType is not { } callerType
                    || argument.FormalParameterType is not { } formalType)
                {
                    continue;
                }

                var passedInKind = WriteLossClassifier.Classify(formalType, callerType, sourceExpression: null, isVariableTarget: true);
                if (passedInKind is { } kind)
                {
                    findings.Add(new ProcCallArgumentMismatchFinding(
                        edge.CallerScopeQualifiedName, edge.CalleeQualifiedName, argument.FormalParameterName,
                        callerVariableName, callerType.ToString(), formalType.ToString(), kind,
                        edge.CallSite.SourcePath, edge.CallSite.Line, edge.CallSite.Column));
                }

                if (!argument.FormalParameterIsOutput || !argument.CallSiteHasOutputKeyword)
                {
                    continue;
                }

                var passedBackKind = WriteLossClassifier.Classify(callerType, formalType, sourceExpression: null, isVariableTarget: true);
                if (passedBackKind is { } outputKind)
                {
                    findings.Add(new ProcCallArgumentMismatchFinding(
                        edge.CallerScopeQualifiedName, edge.CalleeQualifiedName, argument.FormalParameterName,
                        callerVariableName, callerType.ToString(), formalType.ToString(), outputKind,
                        edge.CallSite.SourcePath, edge.CallSite.Line, edge.CallSite.Column));
                }
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
}
