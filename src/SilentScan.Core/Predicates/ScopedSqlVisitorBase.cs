using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;

namespace SilentScan.Core.Predicates;

/// <summary>
/// The FROM-scope and CTE-scope stack machinery shared by every visitor that walks a parsed
/// batch resolving predicates against real columns: the join-scope stack a QuerySpecification/
/// UPDATE/DELETE/MERGE pushes, and the CTE stack a WITH clause (or a trigger's inserted/deleted
/// pseudo-tables) pushes on top of it. Extracted from two near-identical copies
/// (docs/detection-checklist.md "Engineering debt") - <c>TypedPredicateExtractor</c>'s and
/// <c>NonSargablePredicateScanner</c>'s own visitors, which had drifted apart only in the
/// per-visitor state layered around these same calls (a recompile-hint guard, write-loss's own
/// scope-chain snapshot), never in the scope mechanics themselves.
/// <para>
/// Deliberately does NOT also own <c>VisitProcedureOrFunctionBody</c>/<c>VisitTriggerBody</c>/the
/// nine <c>CreateOrAlter*</c> dispatch overrides: those interleave this scope machinery with
/// per-visitor state (parameter/variable resets, the WITH RECOMPILE guard) closely enough that
/// sharing them needs a real template-method design, not a mechanical move - and
/// <c>TypedPredicateExtractor</c>'s and <c>NonSargablePredicateScanner</c>'s own
/// <c>BuildTriggerPseudoTableRelations</c> copies were found to already disagree (only the former
/// ledgers a DDL/LOGON trigger's missing target table) - a real behavioral discrepancy a
/// mechanical merge must not paper over silently. Left as a separate, larger piece.
/// </para>
/// </summary>
internal abstract class ScopedSqlVisitorBase(
    string sourcePath,
    DatabaseCatalog catalog,
    IReadOnlyDictionary<string, ResolvedRelation> resolvedViews,
    SkipLedger? ledger,
    string? currentProcScope,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope) : TSqlFragmentVisitor
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyCteRelations = new Dictionary<string, ResolvedRelation>();

    private readonly Stack<IReadOnlyDictionary<string, ResolvedRelation>> _cteStack = new();

    /// <summary>The active FROM-clause alias scope, innermost on top - shared directly rather than through an accessor, since callers build a whole scope chain by enumerating it.</summary>
    protected readonly Stack<(Dictionary<string, ScopeEntry> ByAlias, List<ScopeEntry> Ordered)> ScopeStack = new();

    /// <summary>The innermost enclosing procedure/function/trigger's qualified name, or null at batch scope - the same key <see cref="Catalog.CatalogBuilder"/> scopes a body's own temp tables/table variables under.</summary>
    protected string? CurrentProcScope { get; set; } = currentProcScope;

    /// <summary>Pushes this WITH clause's own CTEs merged over whatever CTEs are already visible - an empty WITH clause (or none at all) still pushes an unchanged copy of the current top, so every statement kind can pop unconditionally.</summary>
    protected void PushCteScope(WithCtesAndXmlNamespaces? withClause)
    {
        var currentCtes = CurrentCteRelations();
        var ctes = CteResolver.Resolve(withClause, catalog, resolvedViews, sourcePath, ledger, CurrentProcScope);
        _cteStack.Push(ctes.Count == 0 ? currentCtes : MergeCtes(currentCtes, ctes));
    }

    /// <summary>Pushes an already-built CTE-relation set directly - used for a trigger's own inserted/deleted pseudo-tables, which aren't parsed from a WITH clause.</summary>
    protected void PushCteRelations(IReadOnlyDictionary<string, ResolvedRelation> relations) => _cteStack.Push(relations);

    protected void PopCteScope() => _cteStack.Pop();

    protected IReadOnlyDictionary<string, ResolvedRelation> CurrentCteRelations() =>
        _cteStack.Count > 0 ? _cteStack.Peek() : EmptyCteRelations;

    protected FromScopeResolver.ResolutionContext CurrentResolutionContext() =>
        new(catalog, resolvedViews, sourcePath, ledger, CurrentCteRelations(), CurrentProcScope, callerScopeByCalleeScope);

    /// <summary>Inner (this statement's own) CTEs take precedence over an outer statement's same-named CTE, matching how an inner scope shadows an outer one everywhere else in these passes.</summary>
    protected static Dictionary<string, ResolvedRelation> MergeCtes(
        IReadOnlyDictionary<string, ResolvedRelation> outer, IReadOnlyDictionary<string, ResolvedRelation> inner)
    {
        var merged = new Dictionary<string, ResolvedRelation>(outer, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, relation) in inner)
        {
            merged[name] = relation;
        }

        return merged;
    }
}
