using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Predicates.Normalization;

namespace SilentScan.Core.Predicates;

internal sealed class ModuleWalker : TSqlFragmentVisitor
{
    public const string NormalizationEliminatedConstructKind = "predicate eliminated by normalization";

    public const string NormalizationEliminatedLedgerReason =
        "this comparison lives inside a branch the engine's own normalize/simplify pass proves can never contribute a selected row (a same-column contradiction, or a tautology on a confirmed NOT NULL column) - never reaches a real Filter/Seek decision";

    private const string DdlOrLogonTriggerConstructKind = "DDL/LOGON trigger";
    private const string TriggerInsertedDeletedConstructKind = "trigger inserted/deleted";

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyCteRelations = new Dictionary<string, ResolvedRelation>();
    private static readonly IReadOnlySet<TSqlFragment> EmptyDeadPredicateSet = new HashSet<TSqlFragment>();

    private readonly string _sourcePath;
    private readonly DatabaseCatalog _catalog;
    private readonly IReadOnlyDictionary<string, ResolvedRelation> _resolvedViews;
    private readonly SkipLedger? _ledger;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>>? _callerScopeByCalleeScope;
    private readonly AnalysisPass _triggerScopeAnalysisPass;
    private readonly IReadOnlyList<IModuleRule> _rules;

    private readonly Stack<IReadOnlyDictionary<string, ResolvedRelation>> _cteStack = new();
    private readonly Stack<(Dictionary<string, ScopeEntry> ByAlias, List<ScopeEntry> Ordered)> _scopeStack = new();
    private readonly Stack<IReadOnlySet<TSqlFragment>> _deadPredicateStack = new();

    public ModuleWalker(
        string sourcePath,
        DatabaseCatalog catalog,
        IReadOnlyDictionary<string, ResolvedRelation> resolvedViews,
        SkipLedger? ledger,
        string? currentProcScope,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope,
        IReadOnlyList<IModuleRule> rules,
        AnalysisPass triggerScopeAnalysisPass = AnalysisPass.Predicates)
    {
        _sourcePath = sourcePath;
        _catalog = catalog;
        _resolvedViews = resolvedViews;
        _ledger = ledger;
        CurrentProcScope = currentProcScope;
        _callerScopeByCalleeScope = callerScopeByCalleeScope;
        _rules = rules;
        _triggerScopeAnalysisPass = triggerScopeAnalysisPass;
    }

    public string SourcePath => _sourcePath;

    public SkipLedger? Ledger => _ledger;

    public DatabaseCatalog Catalog => _catalog;

    public string? CurrentProcScope { get; private set; }

    public bool IsDeadPredicate(TSqlFragment node) => _deadPredicateStack.Count > 0 && _deadPredicateStack.Peek().Contains(node);

    public FromScopeResolver.ResolutionContext CurrentResolutionContext() =>
        new(_catalog, _resolvedViews, _sourcePath, _ledger, CurrentCteRelations(), CurrentProcScope, _callerScopeByCalleeScope);

    public ScopeChain CurrentScopeChain() =>
        _scopeStack.Select(s => ((IReadOnlyDictionary<string, ScopeEntry>)s.ByAlias, (IReadOnlyList<ScopeEntry>)s.Ordered)).ToList();

    public IReadOnlySet<TSqlFragment> ComputeDeadPredicates(BooleanExpression? searchCondition)
    {
        if (searchCondition is null || _scopeStack.Count == 0)
        {
            return EmptyDeadPredicateSet;
        }

        var scopeChain = CurrentScopeChain();
        return PredicateSurvivalAnalyzer.FindDeadComparisons(searchCondition, columnRef => ResolveColumnFacts(columnRef, scopeChain));
    }

    public PredicateSurvivalAnalyzer.ColumnFacts ResolveColumnFacts(ColumnReferenceExpression columnRef, ScopeChain scopeChain)
    {
        if (ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, _sourcePath, ledger: null, _catalog) is not ColumnProvenance.BaseColumn baseColumn)
        {
            return default;
        }

        var catalogColumn = _catalog.Find(baseColumn.TableQualifiedName, CurrentProcScope)?.FindColumn(baseColumn.ColumnName, _catalog.IdentifierComparer);
        return new PredicateSurvivalAnalyzer.ColumnFacts(
            catalogColumn is null || baseColumn.IsNullableSide ? null : !catalogColumn.IsNullable,
            baseColumn.Type?.Collation?.GuaranteesDistinctLiteralsAreUnequal);
    }

    public static void InspectJoinOnClauses(
        IList<TableReference>? tableReferences,
        ScopeChain scopeChain,
        Action<BooleanExpression, ScopeChain> inspect)
    {
        if (tableReferences is null)
        {
            return;
        }

        foreach (var reference in tableReferences)
        {
            foreach (var join in PredicateTreeWalker.FlattenJoinNodes(reference).Where(j => j.SearchCondition is not null))
            {
                inspect(join.SearchCondition!, scopeChain);
            }
        }
    }

    public static void InspectAllPredicateLocations(
        QuerySpecification node,
        ScopeChain scopeChain,
        Action<BooleanExpression, ScopeChain> inspect)
    {
        if (node.WhereClause?.SearchCondition is { } whereCondition)
        {
            inspect(whereCondition, scopeChain);
        }

        if (node.HavingClause?.SearchCondition is { } havingCondition)
        {
            inspect(havingCondition, scopeChain);
        }

        InspectJoinOnClauses(node.FromClause?.TableReferences, scopeChain, inspect);
    }

    public static void InspectAllPredicateLocations(
        UpdateStatement node,
        ScopeChain scopeChain,
        Action<BooleanExpression, ScopeChain> inspect)
    {
        var spec = node.UpdateSpecification;
        if (spec.WhereClause?.SearchCondition is { } whereCondition)
        {
            inspect(whereCondition, scopeChain);
        }

        InspectJoinOnClauses(spec.FromClause?.TableReferences, scopeChain, inspect);
    }

    public static void InspectAllPredicateLocations(
        DeleteStatement node,
        ScopeChain scopeChain,
        Action<BooleanExpression, ScopeChain> inspect)
    {
        var spec = node.DeleteSpecification;
        if (spec.WhereClause?.SearchCondition is { } whereCondition)
        {
            inspect(whereCondition, scopeChain);
        }

        InspectJoinOnClauses(spec.FromClause?.TableReferences, scopeChain, inspect);
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

    public sealed override void ExplicitVisit(SelectStatement node) =>
        WithCteScope(node.WithCtesAndXmlNamespaces, () =>
        {
            foreach (var rule in _rules)
            {
                rule.OnEnterSelectStatementScope(node, this);
            }

            base.ExplicitVisit(node);

            foreach (var rule in _rules)
            {
                rule.OnLeaveSelectStatementScope(node, this);
            }
        });

    public sealed override void ExplicitVisit(QuerySpecification node) =>
        WithFromScope(node.FromClause, () =>
        {
            var scopeChain = CurrentScopeChain();
            foreach (var rule in _rules)
            {
                rule.OnEnterQuerySpecificationScope(node, scopeChain, this);
            }

            base.ExplicitVisit(node);

            foreach (var rule in _rules)
            {
                rule.OnLeaveQuerySpecificationScope(node, scopeChain, this);
            }
        });

    public sealed override void ExplicitVisit(UpdateStatement node)
    {
        var spec = node.UpdateSpecification;
        WithCteScope(node.WithCtesAndXmlNamespaces, () =>
            WithDataModificationScope(spec.Target, spec.FromClause, () =>
            {
                var scopeChain = CurrentScopeChain();
                foreach (var rule in _rules)
                {
                    rule.OnEnterUpdateStatementScope(node, scopeChain, this);
                }

                base.ExplicitVisit(node);

                foreach (var rule in _rules)
                {
                    rule.OnLeaveUpdateStatementScope(node, scopeChain, this);
                }
            }));
    }

    public sealed override void ExplicitVisit(DeleteStatement node)
    {
        var spec = node.DeleteSpecification;
        WithCteScope(node.WithCtesAndXmlNamespaces, () =>
            WithDataModificationScope(spec.Target, spec.FromClause, () =>
            {
                var scopeChain = CurrentScopeChain();
                foreach (var rule in _rules)
                {
                    rule.OnEnterDeleteStatementScope(node, scopeChain, this);
                }

                base.ExplicitVisit(node);

                foreach (var rule in _rules)
                {
                    rule.OnLeaveDeleteStatementScope(node, scopeChain, this);
                }
            }));
    }

    public sealed override void ExplicitVisit(MergeStatement node)
    {
        var spec = node.MergeSpecification;
        WithCteScope(node.WithCtesAndXmlNamespaces, () =>
            WithMergeScope(spec.Target, spec.TableAlias, spec.TableReference, () =>
            {
                var scopeChain = CurrentScopeChain();
                foreach (var rule in _rules)
                {
                    rule.OnEnterMergeStatementScope(node, scopeChain, this);
                }

                base.ExplicitVisit(node);

                foreach (var rule in _rules)
                {
                    rule.OnLeaveMergeStatementScope(node, scopeChain, this);
                }
            }));
    }

    public sealed override void ExplicitVisit(InsertStatement node) =>
        WithCteScope(node.WithCtesAndXmlNamespaces, () =>
        {
            node.WithCtesAndXmlNamespaces?.Accept(this);

            foreach (var rule in _rules)
            {
                rule.OnEnterInsertStatementScope(node, this);
            }

            node.InsertSpecification.Accept(this);

            foreach (var rule in _rules)
            {
                rule.OnLeaveInsertStatementScope(node, this);
            }
        });

    public sealed override void ExplicitVisit(AssignmentSetClause node)
    {
        foreach (var rule in _rules)
        {
            rule.OnEnterAssignmentSetClause(node, this);
        }

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(SetVariableStatement node)
    {
        foreach (var rule in _rules)
        {
            rule.OnEnterSetVariableStatement(node, this);
        }

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(TSqlBatch node)
    {
        foreach (var rule in _rules)
        {
            rule.OnEnterTSqlBatch(node, this);
        }

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(DeclareVariableStatement node)
    {
        foreach (var rule in _rules)
        {
            rule.OnEnterDeclareVariableStatement(node, this);
        }

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(BooleanNotExpression node)
    {
        foreach (var rule in _rules)
        {
            rule.OnEnterBooleanNotExpression(node, this);
        }

        base.ExplicitVisit(node);

        foreach (var rule in _rules)
        {
            rule.OnLeaveBooleanNotExpression(node, this);
        }
    }

    public sealed override void ExplicitVisit(SearchedCaseExpression node)
    {
        foreach (var rule in _rules)
        {
            rule.OnEnterOperandPosition(node, this);
        }

        base.ExplicitVisit(node);

        foreach (var rule in _rules)
        {
            rule.OnLeaveOperandPosition(node, this);
        }
    }

    public sealed override void ExplicitVisit(SimpleCaseExpression node)
    {
        foreach (var rule in _rules)
        {
            rule.OnEnterOperandPosition(node, this);
        }

        base.ExplicitVisit(node);

        foreach (var rule in _rules)
        {
            rule.OnLeaveOperandPosition(node, this);
        }
    }

    public sealed override void ExplicitVisit(IIfCall node)
    {
        foreach (var rule in _rules)
        {
            rule.OnEnterOperandPosition(node, this);
        }

        base.ExplicitVisit(node);

        foreach (var rule in _rules)
        {
            rule.OnLeaveOperandPosition(node, this);
        }
    }

    public sealed override void ExplicitVisit(CoalesceExpression node)
    {
        foreach (var rule in _rules)
        {
            rule.OnEnterOperandPosition(node, this);
        }

        base.ExplicitVisit(node);

        foreach (var rule in _rules)
        {
            rule.OnLeaveOperandPosition(node, this);
        }
    }

    public sealed override void ExplicitVisit(NullIfExpression node)
    {
        foreach (var rule in _rules)
        {
            rule.OnEnterOperandPosition(node, this);
        }

        base.ExplicitVisit(node);

        foreach (var rule in _rules)
        {
            rule.OnLeaveOperandPosition(node, this);
        }
    }

    public sealed override void ExplicitVisit(WhereClause node)
    {
        foreach (var rule in _rules)
        {
            rule.OnEnterWhereClause(node, this);
        }

        WithPredicateLocation(node.SearchCondition, () => base.ExplicitVisit(node));

        foreach (var rule in _rules)
        {
            rule.OnLeaveWhereClause(node, this);
        }
    }

    public sealed override void ExplicitVisit(HavingClause node)
    {
        foreach (var rule in _rules)
        {
            rule.OnEnterHavingClause(node, this);
        }

        WithPredicateLocation(node.SearchCondition, () => base.ExplicitVisit(node));

        foreach (var rule in _rules)
        {
            rule.OnLeaveHavingClause(node, this);
        }
    }

    public sealed override void ExplicitVisit(QualifiedJoin node)
    {
        node.FirstTableReference?.Accept(this);
        node.SecondTableReference?.Accept(this);

        foreach (var rule in _rules)
        {
            rule.OnEnterJoinSearchCondition(node, this);
        }

        WithPredicateLocation(node.SearchCondition, () => node.SearchCondition?.Accept(this));

        foreach (var rule in _rules)
        {
            rule.OnLeaveJoinSearchCondition(node, this);
        }
    }

    public sealed override void ExplicitVisit(MergeSpecification node)
    {
        node.Target?.Accept(this);
        node.TableReference?.Accept(this);
        node.TopRowFilter?.Accept(this);

        foreach (var rule in _rules)
        {
            rule.OnEnterMergeSearchCondition(node, this);
        }

        node.SearchCondition?.Accept(this);

        foreach (var rule in _rules)
        {
            rule.OnLeaveMergeSearchCondition(node, this);
        }

        foreach (var actionClause in node.ActionClauses)
        {
            actionClause.Accept(this);
        }

        node.OutputClause?.Accept(this);
        node.OutputIntoClause?.Accept(this);
    }

    public sealed override void ExplicitVisit(MergeActionClause node)
    {
        foreach (var rule in _rules)
        {
            rule.OnEnterMergeActionSearchCondition(node, this);
        }

        node.SearchCondition?.Accept(this);

        foreach (var rule in _rules)
        {
            rule.OnLeaveMergeActionSearchCondition(node, this);
        }

        node.Action?.Accept(this);
    }

    public override void Visit(BooleanComparisonExpression node)
    {
        foreach (var rule in _rules)
        {
            rule.OnBooleanComparisonExpression(node, this);
        }

        base.Visit(node);
    }

    public override void Visit(BooleanTernaryExpression node)
    {
        foreach (var rule in _rules)
        {
            rule.OnBooleanTernaryExpression(node, this);
        }

        base.Visit(node);
    }

    public override void Visit(LikePredicate node)
    {
        foreach (var rule in _rules)
        {
            rule.OnLikePredicate(node, this);
        }

        base.Visit(node);
    }

    public override void Visit(InPredicate node)
    {
        foreach (var rule in _rules)
        {
            rule.OnInPredicate(node, this);
        }

        base.Visit(node);
    }

    public override void Visit(SubqueryComparisonPredicate node)
    {
        foreach (var rule in _rules)
        {
            rule.OnSubqueryComparisonPredicate(node, this);
        }

        base.Visit(node);
    }

    public sealed override void ExplicitVisit(SelectSetVariable node)
    {
        foreach (var rule in _rules)
        {
            rule.OnEnterSelectSetVariable(node, this);
        }

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(WhileStatement node)
    {
        foreach (var rule in _rules)
        {
            rule.OnEnterWhileStatement(node, this);
        }

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(IfStatement node)
    {
        foreach (var rule in _rules)
        {
            rule.OnEnterIfStatement(node, this);
        }

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(BooleanBinaryExpression node)
    {
        foreach (var rule in _rules)
        {
            rule.OnEnterBooleanBinaryExpression(node, this);
        }

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(BinaryExpression node)
    {
        foreach (var rule in _rules)
        {
            rule.OnEnterBinaryExpression(node, this);
        }

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(UnaryExpression node)
    {
        foreach (var rule in _rules)
        {
            rule.OnEnterUnaryExpression(node, this);
        }

        base.ExplicitVisit(node);
    }

    private void VisitProcedureOrFunctionBody(ProcedureStatementBodyBase node, SchemaObjectName name)
    {
        var previousScope = CurrentProcScope;
        CurrentProcScope = SchemaObjectNameHelper.Qualify(name);

        foreach (var rule in _rules)
        {
            rule.OnEnterProcedureOrFunctionBody(node, this);
        }

        node.AcceptChildren(this);
        CurrentProcScope = previousScope;

        foreach (var rule in _rules)
        {
            rule.OnLeaveProcedureOrFunctionBody(node, this);
        }
    }

    private void VisitTriggerBody(TriggerStatementBody node, SchemaObjectName name, TriggerObject triggerObject)
    {
        var previousScope = CurrentProcScope;
        CurrentProcScope = SchemaObjectNameHelper.Qualify(name);

        foreach (var rule in _rules)
        {
            rule.OnEnterTriggerBody(node, this);
        }

        if (triggerObject.Name is not { } targetTableName)
        {
            _ledger?.Record(
                _triggerScopeAnalysisPass, _sourcePath, node.StartLine, node.StartColumn,
                DdlOrLogonTriggerConstructKind, $"trigger scope '{triggerObject.TriggerScope}' has no target table - no inserted/deleted pseudo-tables to resolve");
            node.AcceptChildren(this);
            CurrentProcScope = previousScope;

            foreach (var rule in _rules)
            {
                rule.OnLeaveTriggerBody(node, this);
            }

            return;
        }

        PushCteRelations(MergeCtes(CurrentCteRelations(), BuildTriggerPseudoTableRelations(targetTableName, node)));
        node.AcceptChildren(this);
        PopCteScope();

        CurrentProcScope = previousScope;

        foreach (var rule in _rules)
        {
            rule.OnLeaveTriggerBody(node, this);
        }
    }

    public IReadOnlyDictionary<string, ResolvedRelation> BuildTriggerPseudoTableRelations(SchemaObjectName targetTableName, TSqlFragment node)
    {
        var qualifiedName = SchemaObjectNameHelper.Qualify(targetTableName);

        ResolvedRelation relation;
        if (_resolvedViews.TryGetValue(qualifiedName, out var viewRelation))
        {
            relation = FromScopeResolver.ToPseudoTableRelation(viewRelation, qualifiedName);
        }
        else if (_catalog.Find(qualifiedName) is { } table)
        {
            relation = FromScopeResolver.ToPseudoTableRelation(table, qualifiedName);
        }
        else
        {
            _ledger?.Record(
                _triggerScopeAnalysisPass, _sourcePath, node.StartLine, node.StartColumn,
                TriggerInsertedDeletedConstructKind, $"trigger target '{qualifiedName}' has no known DDL and is not a resolved view - inserted/deleted left unresolved");
            return EmptyCteRelations;
        }

        return new Dictionary<string, ResolvedRelation>(StringComparer.OrdinalIgnoreCase)
        {
            ["inserted"] = relation,
            ["deleted"] = relation,
        };
    }

    private void PushCteScope(WithCtesAndXmlNamespaces? withClause)
    {
        var currentCtes = CurrentCteRelations();
        var ctes = CteResolver.Resolve(withClause, _catalog, _resolvedViews, _sourcePath, _ledger, CurrentProcScope);
        _cteStack.Push(ctes.Count == 0 ? currentCtes : MergeCtes(currentCtes, ctes));
    }

    public void PushCteRelations(IReadOnlyDictionary<string, ResolvedRelation> relations) => _cteStack.Push(relations);

    private void PopCteScope() => _cteStack.Pop();

    public IReadOnlyDictionary<string, ResolvedRelation> CurrentCteRelations() =>
        _cteStack.Count > 0 ? _cteStack.Peek() : EmptyCteRelations;

    private static Dictionary<string, ResolvedRelation> MergeCtes(
        IReadOnlyDictionary<string, ResolvedRelation> outer, IReadOnlyDictionary<string, ResolvedRelation> inner)
    {
        var merged = new Dictionary<string, ResolvedRelation>(outer, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, relation) in inner)
        {
            merged[name] = relation;
        }

        return merged;
    }

    public void WithPredicateLocation(BooleanExpression? searchCondition, Action visitChildren)
    {
        _deadPredicateStack.Push(ComputeDeadPredicates(searchCondition));
        try
        {
            visitChildren();
        }
        finally
        {
            _deadPredicateStack.Pop();
        }
    }

    private void WithCteScope(WithCtesAndXmlNamespaces? withClause, Action body)
    {
        PushCteScope(withClause);
        try
        {
            body();
        }
        finally
        {
            PopCteScope();
        }
    }

    private void WithFromScope(FromClause? fromClause, Action body)
    {
        _scopeStack.Push(FromScopeResolver.Resolve(fromClause, CurrentResolutionContext()));
        try
        {
            body();
        }
        finally
        {
            _scopeStack.Pop();
        }
    }

    private void WithDataModificationScope(TableReference target, FromClause? extraFromClause, Action body)
    {
        _scopeStack.Push(FromScopeResolver.ResolveForDataModification(target, extraFromClause, CurrentResolutionContext()));
        try
        {
            body();
        }
        finally
        {
            _scopeStack.Pop();
        }
    }

    private void WithMergeScope(TableReference target, Identifier? targetAlias, TableReference source, Action body)
    {
        _scopeStack.Push(FromScopeResolver.ResolveForMerge(target, targetAlias, source, CurrentResolutionContext()));
        try
        {
            body();
        }
        finally
        {
            _scopeStack.Pop();
        }
    }
}
