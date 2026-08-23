using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public sealed record ProcCallLiteralArgument(string Value, string SourcePath, int StartLine, int StartColumn, int PrefixLength);

public sealed record ProcCallArgument(
    string FormalParameterName, SqlType? FormalParameterType, bool FormalParameterIsOutput,
    string? CallerVariableName, bool IsLiteral, ProcCallLiteralArgument? LiteralArgument = null,
    SqlType? CallerArgumentType = null);

public sealed record ProcCallEdge(
    string? CallerScopeQualifiedName, string CalleeQualifiedName, SourceSpan CallSite, IReadOnlyList<ProcCallArgument> Arguments);

public sealed class ProcCallGraph(IReadOnlyList<ProcCallEdge> edges)
{
    public IReadOnlyList<ProcCallEdge> Edges { get; } = edges;

    public IEnumerable<ProcCallEdge> EdgesCalling(string calleeQualifiedName) =>
        Edges.Where(e => string.Equals(e.CalleeQualifiedName, calleeQualifiedName, StringComparison.OrdinalIgnoreCase));

public ProcCallEdge? EdgeAt(SourceSpan callSite) =>
        Edges.FirstOrDefault(e => e.CallSite == callSite);

public ProcCallEdge? SingleCallSiteFor(string calleeQualifiedName)
    {
        using var enumerator = EdgesCalling(calleeQualifiedName).GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return null;
        }

        var only = enumerator.Current;
        return enumerator.MoveNext() ? null : only;
    }
}
