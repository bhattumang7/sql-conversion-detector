using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Common;
using SilentScan.Core.Predicates.Normalization;

namespace SilentScan.Core.Predicates;

internal abstract class ScopedSqlVisitorBase(
    string sourcePath,
    DatabaseCatalog catalog,
    IReadOnlyDictionary<string, ResolvedRelation> resolvedViews,
    SkipLedger? ledger,
    string? currentProcScope,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope) : TSqlFragmentVisitor
{
    protected const string NormalizationEliminatedConstructKind = "predicate eliminated by normalization";

    protected const string NormalizationEliminatedLedgerReason =
        "this comparison lives inside a branch the engine's own normalize/simplify pass proves can never contribute a selected row (a same-column contradiction, or a tautology on a confirmed NOT NULL column) - never reaches a real Filter/Seek decision";

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyCteRelations = new Dictionary<string, ResolvedRelation>();

    private static readonly IReadOnlySet<TSqlFragment> EmptyDeadPredicateSet = new HashSet<TSqlFragment>();

    private readonly Stack<IReadOnlyDictionary<string, ResolvedRelation>> _cteStack = new();

    protected readonly Stack<(Dictionary<string, ScopeEntry> ByAlias, List<ScopeEntry> Ordered)> ScopeStack = new();

    protected readonly Stack<IReadOnlySet<TSqlFragment>> DeadPredicateStack = new();

    protected string? CurrentProcScope { get; set; } = currentProcScope;

    protected bool IsDeadPredicate(TSqlFragment node) => DeadPredicateStack.Count > 0 && DeadPredicateStack.Peek().Contains(node);

    protected IReadOnlySet<TSqlFragment> ComputeDeadPredicates(BooleanExpression? searchCondition)
    {
        if (searchCondition is null || ScopeStack.Count == 0)
        {
            return EmptyDeadPredicateSet;
        }

        var scopeChain = ScopeStack.Select(s => ((IReadOnlyDictionary<string, ScopeEntry>)s.ByAlias, (IReadOnlyList<ScopeEntry>)s.Ordered)).ToList();
        return PredicateSurvivalAnalyzer.FindDeadComparisons(searchCondition, columnRef => ResolveColumnFacts(columnRef, scopeChain));
    }

    protected PredicateSurvivalAnalyzer.ColumnFacts ResolveColumnFacts(
        ColumnReferenceExpression columnRef, IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
    {
        if (ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, ledger: null) is not ColumnProvenance.BaseColumn baseColumn)
        {
            return default;
        }

        var catalogColumn = catalog.Find(baseColumn.TableQualifiedName, CurrentProcScope)?.FindColumn(baseColumn.ColumnName);
        return new PredicateSurvivalAnalyzer.ColumnFacts(
            catalogColumn is null ? null : !catalogColumn.IsNullable,
            baseColumn.Type?.Collation?.IsCaseSensitive);
    }

    protected void PushCteScope(WithCtesAndXmlNamespaces? withClause)
    {
        var currentCtes = CurrentCteRelations();
        var ctes = CteResolver.Resolve(withClause, catalog, resolvedViews, sourcePath, ledger, CurrentProcScope);
        _cteStack.Push(ctes.Count == 0 ? currentCtes : MergeCtes(currentCtes, ctes));
    }

    protected void PushCteRelations(IReadOnlyDictionary<string, ResolvedRelation> relations) => _cteStack.Push(relations);

    protected void PopCteScope() => _cteStack.Pop();

    protected IReadOnlyDictionary<string, ResolvedRelation> CurrentCteRelations() =>
        _cteStack.Count > 0 ? _cteStack.Peek() : EmptyCteRelations;

    protected FromScopeResolver.ResolutionContext CurrentResolutionContext() =>
        new(catalog, resolvedViews, sourcePath, ledger, CurrentCteRelations(), CurrentProcScope, callerScopeByCalleeScope);

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

    public override void ExplicitVisit(CreateProcedureStatement node) => VisitProcedureOrFunctionBody(node, node.ProcedureReference.Name);

    public override void ExplicitVisit(AlterProcedureStatement node) => VisitProcedureOrFunctionBody(node, node.ProcedureReference.Name);

    public override void ExplicitVisit(CreateOrAlterProcedureStatement node) => VisitProcedureOrFunctionBody(node, node.ProcedureReference.Name);

    public override void ExplicitVisit(CreateFunctionStatement node) => VisitProcedureOrFunctionBody(node, node.Name);

    public override void ExplicitVisit(AlterFunctionStatement node) => VisitProcedureOrFunctionBody(node, node.Name);

    public override void ExplicitVisit(CreateOrAlterFunctionStatement node) => VisitProcedureOrFunctionBody(node, node.Name);

    public override void ExplicitVisit(CreateTriggerStatement node) => VisitTriggerBody(node, node.Name, node.TriggerObject);

    public override void ExplicitVisit(AlterTriggerStatement node) => VisitTriggerBody(node, node.Name, node.TriggerObject);

    public override void ExplicitVisit(CreateOrAlterTriggerStatement node) => VisitTriggerBody(node, node.Name, node.TriggerObject);

    protected virtual void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node)
    {
    }

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

    protected virtual void OnEnterTriggerBody(TriggerStatementBody node)
    {
    }

    private const string DdlOrLogonTriggerConstructKind = "DDL/LOGON trigger";
    private const string TriggerInsertedDeletedConstructKind = "trigger inserted/deleted";

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
