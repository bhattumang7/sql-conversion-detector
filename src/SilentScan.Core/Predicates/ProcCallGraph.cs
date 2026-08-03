using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

/// <summary>
/// A string literal argument's exact source provenance - kept alongside its raw value so a
/// finding produced from analyzing dynamic SQL built from a SEEDED callee parameter (see
/// <see cref="DynamicSqlScanner"/>) still points at the literal's real location in the CALLER's
/// own source, not the callee's EXEC site or its own parameter declaration - neither of which is
/// where the string actually came from. Mirrors DynamicSqlScanner's own private LiteralSegment
/// shape, duplicated here (rather than shared) because this pass sits below DynamicSqlScanner in
/// the dependency order and must not depend on that scanner's internals.
/// </summary>
public sealed record ProcCallLiteralArgument(string Value, string SourcePath, int StartLine, int StartColumn, int PrefixLength);

/// <summary>
/// One actual argument at an <c>EXEC</c> call site, matched to the callee's own declared formal
/// parameter (by name for <c>@P = value</c>, by position otherwise). <paramref name="CallerVariableName"/>
/// is the bare variable name passed, when the argument value is a plain <c>VariableReference</c> -
/// null for a literal, an expression, a subquery, or anything else this pass doesn't need to
/// distinguish further; <paramref name="IsLiteral"/> is true only for a genuine literal value.
/// <paramref name="LiteralArgument"/> is populated only when the actual value is specifically a
/// <c>StringLiteral</c> - the only literal shape dynamic-SQL constant-folding (Tier A/C) can ever
/// consume; a numeric/date/other literal leaves this null even though <paramref name="IsLiteral"/>
/// is still true, since seeding a string-concatenation fold with a non-string literal's raw text
/// would be a guess about implicit conversion this project's soundness-first rule forbids.
/// Neither <paramref name="CallerVariableName"/> nor <paramref name="FormalParameterType"/> claims
/// to know the caller variable's own value - only <paramref name="LiteralArgument"/> ever supplies
/// a concrete value for cross-call-edge seeding.
/// </summary>
public sealed record ProcCallArgument(
    string FormalParameterName, SqlType? FormalParameterType, bool FormalParameterIsOutput,
    string? CallerVariableName, bool IsLiteral, ProcCallLiteralArgument? LiteralArgument = null);

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

    /// <summary>
    /// The single call site for <paramref name="calleeQualifiedName"/> this scan actually saw -
    /// null when there are zero (nothing calls it, or nothing this scan resolved to it) or MORE
    /// THAN ONE. A value seen at exactly one call site within THIS scan is the only case a
    /// literal argument can be treated as a single constant for the callee's own analysis -
    /// with two or more call sites there is no single value to seed, and "we only saw one" is
    /// itself scan-scope-relative (an unparsed caller, application code, or a synonym this scan
    /// didn't resolve could always add another), never a runtime guarantee.
    /// </summary>
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
