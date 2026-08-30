using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Common;

namespace SilentScan.Core.Lineage;

#pragma warning disable CS9113
internal abstract class ScopedRelationWalker(
    string sourcePath,
    DatabaseCatalog catalog,
    IReadOnlyDictionary<string, ResolvedRelation> resolvedViews,
    SkipLedger? ledger,
    string? currentProcScope,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope) : TSqlFragmentVisitor
#pragma warning restore CS9113
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyCteRelations = new Dictionary<string, ResolvedRelation>();

    private readonly Stack<IReadOnlyDictionary<string, ResolvedRelation>> _cteStack = new();

    protected string? CurrentProcScope { get; set; } = currentProcScope;

    protected virtual AnalysisPass TriggerScopeAnalysisPass => AnalysisPass.Lineage;

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
        var previousScope = CurrentProcScope;
        CurrentProcScope = SchemaObjectNameHelper.Qualify(name);
        OnEnterProcedureOrFunctionBody(node);
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
        var previousScope = CurrentProcScope;
        CurrentProcScope = SchemaObjectNameHelper.Qualify(name);
        OnEnterTriggerBody(node);

        if (triggerObject.Name is not { } targetTableName)
        {
            ledger?.Record(
                TriggerScopeAnalysisPass, sourcePath, node.StartLine, node.StartColumn,
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
                TriggerScopeAnalysisPass, sourcePath, node.StartLine, node.StartColumn,
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
