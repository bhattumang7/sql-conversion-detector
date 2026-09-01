using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public sealed record ProcCallLiteralArgument(string Value, string SourcePath, int StartLine, int StartColumn, int PrefixLength);

public sealed record ProcCallArgument(
    string FormalParameterName, SqlType? FormalParameterType, bool FormalParameterIsOutput,
    string? CallerVariableName, bool IsLiteral, ProcCallLiteralArgument? LiteralArgument = null,
    SqlType? CallerArgumentType = null, bool CallSiteHasOutputKeyword = true,
    ScalarExpression? CallerArgumentExpression = null, bool CallerVariableWasAssignedBeforeCall = true,
    bool CallerFlowApproximate = false, string? FormalTableTypeQualifiedName = null);

public sealed record ProcCallEdge(
    string? CallerScopeQualifiedName, string CalleeQualifiedName, SourceSpan CallSite, IReadOnlyList<ProcCallArgument> Arguments);

public sealed record SpExecuteSqlParameterBinding(
    string ParameterName, SqlType? DeclaredType, bool DeclaredIsOutput,
    string? CallerVariableName, SqlType? CallerArgumentType, bool CallSiteHasOutputKeyword,
    ScalarExpression? CallerArgumentExpression, bool CallerVariableWasAssignedBeforeCall, bool CallerFlowApproximate);

public sealed record SpExecuteSqlCallSite(
    string? CallerScopeQualifiedName, SourceSpan CallSite, IReadOnlyList<SpExecuteSqlParameterBinding> Bindings);

public sealed class ProcCallGraph(
    IReadOnlyList<ProcCallEdge> edges, IReadOnlyList<SpExecuteSqlCallSite>? spExecuteSqlCallSites = null, StringComparer? identifierComparer = null)
{
    private readonly StringComparer _identifierComparer = identifierComparer ?? StringComparer.OrdinalIgnoreCase;

    public IReadOnlyList<ProcCallEdge> Edges { get; } = edges;

    public IReadOnlyList<SpExecuteSqlCallSite> SpExecuteSqlCallSites { get; } = spExecuteSqlCallSites ?? [];

    public IEnumerable<ProcCallEdge> EdgesCalling(string calleeQualifiedName) =>
        Edges.Where(e => _identifierComparer.Equals(e.CalleeQualifiedName, calleeQualifiedName));

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
