using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Walks an already-built <see cref="ProcCallGraph"/> (no catalog needed - every argument's
/// types are already resolved onto the edge) and flags a call-site argument whose caller-side
/// declared type risks silent data loss against the callee's own formal parameter type, per
/// <see cref="WriteLossClassifier"/> - the same classifier an INSERT/UPDATE assignment uses,
/// reused here because a parameter binding is exactly that shape (a source value assigned into a
/// declared target). Only a genuine variable-reference argument with BOTH sides' types resolved
/// can be classified - a literal argument, an expression, or an unresolvable type is silently
/// skipped (never guessed), matching every other stream's "Unknown over guesses" discipline.
/// </summary>
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

                // A source expression is unavailable here (only the variable's own declared type
                // is known, not the specific literal it might currently hold) - WriteLossClassifier
                // treats a non-literal source as always flagged when the type pair itself is
                // risky, exactly the "what does this type pair MAKE POSSIBLE" framing this
                // project's other classifiers already use.
                var kind = WriteLossClassifier.Classify(formalType, callerType, sourceExpression: null);
                if (kind is null)
                {
                    continue;
                }

                findings.Add(new ProcCallArgumentMismatchFinding(
                    edge.CallerScopeQualifiedName, edge.CalleeQualifiedName, argument.FormalParameterName,
                    callerVariableName, callerType.ToString(), formalType.ToString(), kind.Value,
                    edge.CallSite.SourcePath, edge.CallSite.Line, edge.CallSite.Column));
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
