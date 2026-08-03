using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

/// <summary>
/// One actual argument at an <c>EXEC</c> call site, matched to the callee's own declared formal
/// parameter (by name for <c>@P = value</c>, by position otherwise). <paramref name="CallerVariableName"/>
/// is the bare variable name passed, when the argument value is a plain <c>VariableReference</c> -
/// null for a literal, an expression, a subquery, or anything else this pass doesn't need to
/// distinguish further; <paramref name="IsLiteral"/> is true only for a genuine literal value.
/// Neither field claims to know the caller variable's TYPE - resolving that (and using it to seed
/// the callee's own analysis) is a separate, later concern this graph only supplies the raw
/// material for.
/// </summary>
public sealed record ProcCallArgument(
    string FormalParameterName, SqlType? FormalParameterType, bool FormalParameterIsOutput, string? CallerVariableName, bool IsLiteral);

/// <summary>
/// One <c>EXEC</c> call site whose target resolved to a procedure this scan actually saw declared
/// (<see cref="DatabaseCatalog.TryGetProcedureParameters"/>) - <paramref name="CallerScopeQualifiedName"/>
/// is the enclosing procedure/function/trigger the call site was found inside, or null for a
/// top-level ad-hoc batch.
/// </summary>
public sealed record ProcCallEdge(
    string? CallerScopeQualifiedName, string CalleeQualifiedName, SourceSpan CallSite, IReadOnlyList<ProcCallArgument> Arguments);

/// <summary>Every call-graph edge discovered across a scan, queryable by callee - the shape the seeding/tracing work built on top of this (CLAUDE.md roadmap) needs: "every known call site that could feed CalleeProc a value".</summary>
public sealed class ProcCallGraph(IReadOnlyList<ProcCallEdge> edges)
{
    public IReadOnlyList<ProcCallEdge> Edges { get; } = edges;

    public IEnumerable<ProcCallEdge> EdgesCalling(string calleeQualifiedName) =>
        Edges.Where(e => string.Equals(e.CalleeQualifiedName, calleeQualifiedName, StringComparison.OrdinalIgnoreCase));
}
