using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;

namespace SilentScan.Core.Predicates;

/// <summary>
/// The FROM-scope and CTE-scope stack machinery shared by every visitor that walks a parsed
/// batch resolving predicates against real columns: the join-scope stack a QuerySpecification/
/// UPDATE/DELETE/MERGE pushes, the CTE stack a WITH clause (or a trigger's inserted/deleted
/// pseudo-tables) pushes on top of it, and proc/function/trigger body scoping (the nine
/// <c>CreateOrAlter*</c> dispatch overrides, <see cref="VisitProcedureOrFunctionBody"/>,
/// <see cref="VisitTriggerBody"/>). Extracted from two near-identical copies
/// (docs/detection-checklist.md "Engineering debt") - <c>TypedPredicateExtractor</c>'s and
/// <c>NonSargablePredicateScanner</c>'s own visitors, which had drifted apart only in per-visitor
/// state layered around the same calls (parameter/variable resets, the WITH RECOMPILE guard),
/// exposed here as the <c>On*ProcedureOrFunctionBody</c>/<c>On*TriggerBody</c> hooks a subclass
/// overrides for exactly that extra state - never in the scope mechanics themselves.
/// <see cref="BuildTriggerPseudoTableRelations"/> is shared outright, not hooked: the two
/// original copies had already drifted (only one ledgered a DDL/LOGON trigger's missing target
/// table) - a real, pre-existing discrepancy, not a design difference worth preserving, so both
/// subclasses now get the more honest behavior (ledger the gap) rather than the merge silently
/// picking one subclass's behavior for the other.
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

    // ScriptDOM's Accept/ExplicitVisit dispatch binds at compile time to the most specific
    // overload that exists on the concrete visitor type, so overriding only the common
    // ProcedureStatementBodyBase/TriggerStatementBody base type here (rather than each of these
    // nine leaf types) would never fire - a real-world corpus routinely ships a body-less
    // "CREATE PROCEDURE ... AS RETURN 0" stub followed by the real body via ALTER PROCEDURE, so
    // both creation and alteration forms need their own override reaching the same shared body.
    public override void ExplicitVisit(CreateProcedureStatement node) => VisitProcedureOrFunctionBody(node, node.ProcedureReference.Name);

    public override void ExplicitVisit(AlterProcedureStatement node) => VisitProcedureOrFunctionBody(node, node.ProcedureReference.Name);

    public override void ExplicitVisit(CreateOrAlterProcedureStatement node) => VisitProcedureOrFunctionBody(node, node.ProcedureReference.Name);

    public override void ExplicitVisit(CreateFunctionStatement node) => VisitProcedureOrFunctionBody(node, node.Name);

    public override void ExplicitVisit(AlterFunctionStatement node) => VisitProcedureOrFunctionBody(node, node.Name);

    public override void ExplicitVisit(CreateOrAlterFunctionStatement node) => VisitProcedureOrFunctionBody(node, node.Name);

    public override void ExplicitVisit(CreateTriggerStatement node) => VisitTriggerBody(node, node.Name, node.TriggerObject);

    public override void ExplicitVisit(AlterTriggerStatement node) => VisitTriggerBody(node, node.Name, node.TriggerObject);

    public override void ExplicitVisit(CreateOrAlterTriggerStatement node) => VisitTriggerBody(node, node.Name, node.TriggerObject);

    /// <summary>
    /// Hook for a subclass's own per-body reset (TypedPredicateExtractor: clearing declared
    /// variables/formal parameters, recording this body's own parameters, and computing whether
    /// WITH RECOMPILE is in effect for its duration). No-op by default - NonSargablePredicateScanner
    /// tracks none of this, since Tier-1's syntactic patterns never need variable typing.
    /// </summary>
    protected virtual void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node)
    {
    }

    /// <summary>Restores whatever state <see cref="OnEnterProcedureOrFunctionBody"/> changed, once this body's own children have been walked.</summary>
    protected virtual void OnLeaveProcedureOrFunctionBody(ProcedureStatementBodyBase node)
    {
    }

    private void VisitProcedureOrFunctionBody(ProcedureStatementBodyBase node, SchemaObjectName name)
    {
        OnEnterProcedureOrFunctionBody(node);
        var previousScope = CurrentProcScope;
        CurrentProcScope = SchemaObjectNameHelper.Qualify(name);
        node.AcceptChildren(this);
        CurrentProcScope = previousScope;
        OnLeaveProcedureOrFunctionBody(node);
    }

    /// <summary>Hook for a subclass's own per-body reset - see <see cref="OnEnterProcedureOrFunctionBody"/>. Triggers can never carry WITH RECOMPILE, so TypedPredicateExtractor's own override only clears variables/parameters here, not the recompile guard.</summary>
    protected virtual void OnEnterTriggerBody(TriggerStatementBody node)
    {
    }

    private const string DdlOrLogonTriggerConstructKind = "DDL/LOGON trigger";
    private const string TriggerInsertedDeletedConstructKind = "trigger inserted/deleted";

    /// <summary>
    /// inserted/deleted are visible throughout the whole trigger body, not just a single top-level
    /// SELECT - pushed onto the same CTE stack a real WITH clause uses (both are resolved
    /// identically by <see cref="FromScopeResolver"/>, a named relation checked before the
    /// catalog/views), so nested subqueries inherit them the same way a CTE would. A DDL
    /// (ON DATABASE/ON ALL SERVER) or LOGON trigger has no target object at all - it gets its
    /// data from EVENTDATA(), not a pseudo-table - so there is nothing to seed; the body is still
    /// walked, since it may contain ordinary predicates against real tables, and the gap is
    /// ledgered rather than silently skipped.
    /// </summary>
    private void VisitTriggerBody(TriggerStatementBody node, SchemaObjectName name, TriggerObject triggerObject)
    {
        OnEnterTriggerBody(node);
        var previousScope = CurrentProcScope;
        CurrentProcScope = SchemaObjectNameHelper.Qualify(name);

        if (triggerObject.Name is not { } targetTableName)
        {
            ledger?.Record(
                AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn,
                DdlOrLogonTriggerConstructKind, $"trigger scope '{triggerObject.TriggerScope}' has no target table - no inserted/deleted pseudo-tables to resolve");
            node.AcceptChildren(this);
            CurrentProcScope = previousScope;
            return;
        }

        PushCteRelations(MergeCtes(CurrentCteRelations(), BuildTriggerPseudoTableRelations(targetTableName, node)));
        node.AcceptChildren(this);
        PopCteScope();

        CurrentProcScope = previousScope;
    }

    /// <summary>
    /// inserted/deleted are shaped exactly like the trigger's own target table or view: a
    /// predicate against inserted.Col reflects that real column's type, but NOT its index -
    /// inserted/deleted are a version-store rowset with no index of their own, so this uses
    /// <see cref="FromScopeResolver.ToPseudoTableRelation(Catalog.CatalogTable?, string)"/> rather
    /// than the ordinary FROM-clause conversion, which would wrongly inherit a real index. An
    /// INSTEAD OF trigger can target a VIEW rather than a table - <see cref="DatabaseCatalog"/>
    /// holds no views, so <paramref name="targetTableName"/> is checked against
    /// <c>resolvedViews</c> (the same lookup <see cref="FromScopeResolver"/>'s own
    /// NamedTableReference case checks) before falling back to the catalog.
    /// </summary>
    protected IReadOnlyDictionary<string, ResolvedRelation> BuildTriggerPseudoTableRelations(SchemaObjectName targetTableName, TSqlFragment node)
    {
        var qualifiedName = SchemaObjectNameHelper.Qualify(targetTableName);

        ResolvedRelation relation;
        if (resolvedViews.TryGetValue(qualifiedName, out var viewRelation))
        {
            relation = FromScopeResolver.ToPseudoTableRelation(viewRelation, qualifiedName);
        }
        else if (catalog.Find(qualifiedName) is { } table)
        {
            relation = FromScopeResolver.ToPseudoTableRelation(table, qualifiedName);
        }
        else
        {
            ledger?.Record(
                AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn,
                TriggerInsertedDeletedConstructKind, $"trigger target '{qualifiedName}' has no known DDL and is not a resolved view - inserted/deleted left unresolved");
            return EmptyCteRelations;
        }

        return new Dictionary<string, ResolvedRelation>(StringComparer.OrdinalIgnoreCase)
        {
            ["inserted"] = relation,
            ["deleted"] = relation,
        };
    }
}
