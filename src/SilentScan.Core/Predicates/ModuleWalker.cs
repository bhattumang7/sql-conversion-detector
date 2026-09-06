global using ScopeChain = System.Collections.Generic.IReadOnlyList<(
    System.Collections.Generic.IReadOnlyDictionary<string, SilentScan.Core.Lineage.ScopeEntry> ByAlias,
    System.Collections.Generic.IReadOnlyList<SilentScan.Core.Lineage.ScopeEntry> Ordered)>;

using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Predicates.Normalization;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public sealed class ModuleWalker : TSqlFragmentVisitor
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
    private readonly Dictionary<IModuleRule, Exception> _crashed = [];

    public ModuleWalker(
        string sourcePath,
        DatabaseCatalog catalog,
        IReadOnlyDictionary<string, ResolvedRelation> resolvedViews,
        IReadOnlyList<IModuleRule> rules,
        ModuleWalkerCallerContext? callerContext = null,
        AnalysisPass triggerScopeAnalysisPass = AnalysisPass.Predicates)
    {
        var caller = callerContext ?? ModuleWalkerCallerContext.None;
        _sourcePath = sourcePath;
        _catalog = catalog;
        _resolvedViews = resolvedViews;
        _ledger = caller.Ledger;
        CurrentProcScope = caller.CurrentProcScope;
        _callerScopeByCalleeScope = caller.CallerScopeByCalleeScope;
        _rules = rules;
        _triggerScopeAnalysisPass = triggerScopeAnalysisPass;
    }

    public string SourcePath => _sourcePath;

    public SkipLedger? Ledger => _ledger;

    public DatabaseCatalog Catalog => _catalog;

    public string? CurrentProcScope { get; private set; }

    public IReadOnlyDictionary<IModuleRule, Exception> CrashedRules => _crashed;

    public bool IsDeadPredicate(TSqlFragment node) => _deadPredicateStack.Count > 0 && _deadPredicateStack.Peek().Contains(node);

    private void Dispatch(Action<IModuleRule> hook)
    {
        foreach (var rule in _rules)
        {
            if (_crashed.ContainsKey(rule))
            {
                continue;
            }

            try
            {
                hook(rule);
            }
            catch (Exception ex)
            {
                _crashed[rule] = ex;
            }
        }
    }

    internal FromScopeResolver.ResolutionContext CurrentResolutionContext() =>
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
            baseColumn.Type?.Collation?.GuaranteesDistinctLiteralsAreUnequal,
            baseColumn.Type?.Category is SqlTypeCategory.Text or SqlTypeCategory.NText or SqlTypeCategory.Image);
    }

    public (string TableQualifiedName, CatalogColumn Column)? ResolveCatalogColumn(ColumnReferenceExpression columnRef, ScopeChain scopeChain)
    {
        if (ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, _sourcePath, ledger: null, _catalog) is not ColumnProvenance.BaseColumn baseColumn)
        {
            return null;
        }

        var catalogColumn = _catalog.Find(baseColumn.TableQualifiedName, CurrentProcScope)?.FindColumn(baseColumn.ColumnName, _catalog.IdentifierComparer);
        return catalogColumn is null ? null : (baseColumn.TableQualifiedName, catalogColumn);
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

    public override void ExplicitVisit(CreateProcedureStatement node)
    {
        Dispatch(rule => rule.OnEnterCreateProcedureStatement(node, this));

        VisitProcedureOrFunctionBody(node, node.ProcedureReference.Name);

        Dispatch(rule => rule.OnLeaveCreateProcedureStatement(node, this));
    }

    public override void ExplicitVisit(AlterProcedureStatement node)
    {
        Dispatch(rule => rule.OnEnterAlterProcedureStatement(node, this));

        VisitProcedureOrFunctionBody(node, node.ProcedureReference.Name);

        Dispatch(rule => rule.OnLeaveAlterProcedureStatement(node, this));
    }

    public override void ExplicitVisit(CreateOrAlterProcedureStatement node)
    {
        Dispatch(rule => rule.OnEnterCreateOrAlterProcedureStatement(node, this));

        VisitProcedureOrFunctionBody(node, node.ProcedureReference.Name);

        Dispatch(rule => rule.OnLeaveCreateOrAlterProcedureStatement(node, this));
    }

    public override void ExplicitVisit(CreateFunctionStatement node)
    {
        Dispatch(rule => rule.OnEnterCreateFunctionStatement(node, this));

        VisitProcedureOrFunctionBody(node, node.Name);
    }

    public override void ExplicitVisit(AlterFunctionStatement node)
    {
        Dispatch(rule => rule.OnEnterAlterFunctionStatement(node, this));

        VisitProcedureOrFunctionBody(node, node.Name);
    }

    public override void ExplicitVisit(CreateOrAlterFunctionStatement node)
    {
        Dispatch(rule => rule.OnEnterCreateOrAlterFunctionStatement(node, this));

        VisitProcedureOrFunctionBody(node, node.Name);
    }

    public override void ExplicitVisit(CreateTriggerStatement node) => VisitTriggerBody(node, node.Name, node.TriggerObject);

    public override void ExplicitVisit(AlterTriggerStatement node) => VisitTriggerBody(node, node.Name, node.TriggerObject);

    public override void ExplicitVisit(CreateOrAlterTriggerStatement node) => VisitTriggerBody(node, node.Name, node.TriggerObject);

    public sealed override void ExplicitVisit(SelectStatement node) =>
        WithCteScope(node.WithCtesAndXmlNamespaces, () =>
        {
            Dispatch(rule => rule.OnEnterSelectStatementScope(node, this));

            base.ExplicitVisit(node);

            Dispatch(rule => rule.OnLeaveSelectStatementScope(node, this));
        });

    public sealed override void ExplicitVisit(QuerySpecification node) =>
        WithFromScope(node.FromClause, () =>
        {
            var scopeChain = CurrentScopeChain();
            Dispatch(rule => rule.OnEnterQuerySpecificationScope(node, scopeChain, this));

            base.ExplicitVisit(node);

            Dispatch(rule => rule.OnLeaveQuerySpecificationScope(node, scopeChain, this));
        });

    public sealed override void ExplicitVisit(UpdateStatement node)
    {
        var spec = node.UpdateSpecification;
        WithCteScope(node.WithCtesAndXmlNamespaces, () =>
            WithDataModificationScope(spec.Target, spec.FromClause, () =>
            {
                var scopeChain = CurrentScopeChain();
                Dispatch(rule => rule.OnEnterUpdateStatementScope(node, scopeChain, this));

                base.ExplicitVisit(node);

                Dispatch(rule => rule.OnLeaveUpdateStatementScope(node, scopeChain, this));
            }));
    }

    public sealed override void ExplicitVisit(DeleteStatement node)
    {
        var spec = node.DeleteSpecification;
        WithCteScope(node.WithCtesAndXmlNamespaces, () =>
            WithDataModificationScope(spec.Target, spec.FromClause, () =>
            {
                var scopeChain = CurrentScopeChain();
                Dispatch(rule => rule.OnEnterDeleteStatementScope(node, scopeChain, this));

                base.ExplicitVisit(node);

                Dispatch(rule => rule.OnLeaveDeleteStatementScope(node, scopeChain, this));
            }));
    }

    public sealed override void ExplicitVisit(MergeStatement node)
    {
        var spec = node.MergeSpecification;
        WithCteScope(node.WithCtesAndXmlNamespaces, () =>
            WithMergeScope(spec.Target, spec.TableAlias, spec.TableReference, () =>
            {
                var scopeChain = CurrentScopeChain();
                Dispatch(rule => rule.OnEnterMergeStatementScope(node, scopeChain, this));

                base.ExplicitVisit(node);

                Dispatch(rule => rule.OnLeaveMergeStatementScope(node, scopeChain, this));
            }));
    }

    public sealed override void ExplicitVisit(InsertStatement node) =>
        WithCteScope(node.WithCtesAndXmlNamespaces, () =>
        {
            node.WithCtesAndXmlNamespaces?.Accept(this);

            Dispatch(rule => rule.OnEnterInsertStatementScope(node, this));

            node.InsertSpecification.Accept(this);

            Dispatch(rule => rule.OnLeaveInsertStatementScope(node, this));
        });

    public sealed override void ExplicitVisit(InsertMergeAction node)
    {
        Dispatch(rule => rule.OnEnterInsertMergeAction(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(AssignmentSetClause node)
    {
        Dispatch(rule => rule.OnEnterAssignmentSetClause(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(SetVariableStatement node)
    {
        Dispatch(rule => rule.OnEnterSetVariableStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(TSqlBatch node)
    {
        Dispatch(rule => rule.OnEnterTSqlBatch(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(DeclareVariableStatement node)
    {
        Dispatch(rule => rule.OnEnterDeclareVariableStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(BooleanNotExpression node)
    {
        Dispatch(rule => rule.OnEnterBooleanNotExpression(node, this));

        base.ExplicitVisit(node);

        Dispatch(rule => rule.OnLeaveBooleanNotExpression(node, this));
    }

    public sealed override void ExplicitVisit(SearchedCaseExpression node)
    {
        Dispatch(rule => rule.OnEnterOperandPosition(node, this));

        base.ExplicitVisit(node);

        Dispatch(rule => rule.OnLeaveOperandPosition(node, this));
    }

    public sealed override void ExplicitVisit(SimpleCaseExpression node)
    {
        Dispatch(rule => rule.OnEnterOperandPosition(node, this));

        base.ExplicitVisit(node);

        Dispatch(rule => rule.OnLeaveOperandPosition(node, this));
    }

    public sealed override void ExplicitVisit(IIfCall node)
    {
        Dispatch(rule => rule.OnEnterOperandPosition(node, this));

        base.ExplicitVisit(node);

        Dispatch(rule => rule.OnLeaveOperandPosition(node, this));
    }

    public sealed override void ExplicitVisit(CoalesceExpression node)
    {
        Dispatch(rule => rule.OnEnterOperandPosition(node, this));

        base.ExplicitVisit(node);

        Dispatch(rule => rule.OnLeaveOperandPosition(node, this));
    }

    public sealed override void ExplicitVisit(NullIfExpression node)
    {
        Dispatch(rule => rule.OnEnterOperandPosition(node, this));

        base.ExplicitVisit(node);

        Dispatch(rule => rule.OnLeaveOperandPosition(node, this));
    }

    public sealed override void ExplicitVisit(WhereClause node)
    {
        Dispatch(rule => rule.OnEnterWhereClause(node, this));

        WithPredicateLocation(node.SearchCondition, () => base.ExplicitVisit(node));

        Dispatch(rule => rule.OnLeaveWhereClause(node, this));
    }

    public sealed override void ExplicitVisit(HavingClause node)
    {
        Dispatch(rule => rule.OnEnterHavingClause(node, this));

        WithPredicateLocation(node.SearchCondition, () => base.ExplicitVisit(node));

        Dispatch(rule => rule.OnLeaveHavingClause(node, this));
    }

    public sealed override void ExplicitVisit(QualifiedJoin node)
    {
        node.FirstTableReference?.Accept(this);
        node.SecondTableReference?.Accept(this);

        Dispatch(rule => rule.OnEnterJoinSearchCondition(node, this));

        WithPredicateLocation(node.SearchCondition, () => node.SearchCondition?.Accept(this));

        Dispatch(rule => rule.OnLeaveJoinSearchCondition(node, this));
    }

    public sealed override void ExplicitVisit(MergeSpecification node)
    {
        node.Target?.Accept(this);
        node.TableReference?.Accept(this);
        node.TopRowFilter?.Accept(this);

        Dispatch(rule => rule.OnEnterMergeSearchCondition(node, this));

        node.SearchCondition?.Accept(this);

        Dispatch(rule => rule.OnLeaveMergeSearchCondition(node, this));

        foreach (var actionClause in node.ActionClauses)
        {
            actionClause.Accept(this);
        }

        node.OutputClause?.Accept(this);
        node.OutputIntoClause?.Accept(this);
    }

    public sealed override void ExplicitVisit(MergeActionClause node)
    {
        Dispatch(rule => rule.OnEnterMergeActionSearchCondition(node, this));

        node.SearchCondition?.Accept(this);

        Dispatch(rule => rule.OnLeaveMergeActionSearchCondition(node, this));

        node.Action?.Accept(this);
    }

    public sealed override void ExplicitVisit(SelectSetVariable node)
    {
        Dispatch(rule => rule.OnEnterSelectSetVariable(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(WhileStatement node)
    {
        Dispatch(rule => rule.OnEnterWhileStatement(node, this));

        base.ExplicitVisit(node);

        Dispatch(rule => rule.OnLeaveWhileStatement(node, this));
    }

    public sealed override void ExplicitVisit(IfStatement node)
    {
        Dispatch(rule => rule.OnEnterIfStatement(node, this));

        base.ExplicitVisit(node);

        Dispatch(rule => rule.OnLeaveIfStatement(node, this));
    }

    public sealed override void ExplicitVisit(TryCatchStatement node)
    {
        Dispatch(rule => rule.OnEnterTryCatchStatement(node, this));

        node.TryStatements?.Accept(this);

        Dispatch(rule => rule.OnEnterCatchBlock(node, this));
        node.CatchStatements?.Accept(this);
        Dispatch(rule => rule.OnLeaveCatchBlock(node, this));

        Dispatch(rule => rule.OnLeaveTryCatchStatement(node, this));
    }

    public sealed override void ExplicitVisit(BooleanBinaryExpression node)
    {
        Dispatch(rule => rule.OnEnterBooleanBinaryExpression(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(BinaryExpression node)
    {
        Dispatch(rule => rule.OnEnterBinaryExpression(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(UnaryExpression node)
    {
        Dispatch(rule => rule.OnEnterUnaryExpression(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(FromClause node)
    {
        Dispatch(rule => rule.OnEnterFromClause(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(AlterTableSwitchStatement node)
    {
        Dispatch(rule => rule.OnEnterAlterTableSwitchStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(AlterTableChangeTrackingModificationStatement node)
    {
        Dispatch(rule => rule.OnEnterAlterTableChangeTrackingModificationStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(CreateXmlSchemaCollectionStatement node)
    {
        Dispatch(rule => rule.OnEnterCreateXmlSchemaCollectionStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(AlterXmlSchemaCollectionStatement node)
    {
        Dispatch(rule => rule.OnEnterAlterXmlSchemaCollectionStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(AlterSchemaStatement node)
    {
        Dispatch(rule => rule.OnEnterAlterSchemaStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(AlterTableRebuildStatement node)
    {
        Dispatch(rule => rule.OnEnterAlterTableRebuildStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(AlterIndexStatement node)
    {
        Dispatch(rule => rule.OnEnterAlterIndexStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(DeclareCursorStatement node)
    {
        Dispatch(rule => rule.OnEnterDeclareCursorStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(StatementList node)
    {
        Dispatch(rule => rule.OnEnterStatementList(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(DeclareTableVariableStatement node)
    {
        Dispatch(rule => rule.OnEnterDeclareTableVariableStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(BinaryQueryExpression node)
    {
        Dispatch(rule => rule.OnEnterBinaryQueryExpression(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(PredicateSetStatement node)
    {
        Dispatch(rule => rule.OnEnterPredicateSetStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(CreateViewStatement node)
    {
        Dispatch(rule => rule.OnEnterCreateViewStatement(node, this));

        base.ExplicitVisit(node);

        Dispatch(rule => rule.OnLeaveCreateViewStatement(node, this));
    }

    public sealed override void ExplicitVisit(AlterViewStatement node)
    {
        Dispatch(rule => rule.OnEnterAlterViewStatement(node, this));

        base.ExplicitVisit(node);

        Dispatch(rule => rule.OnLeaveAlterViewStatement(node, this));
    }

    public sealed override void ExplicitVisit(CreateOrAlterViewStatement node)
    {
        Dispatch(rule => rule.OnEnterCreateOrAlterViewStatement(node, this));

        base.ExplicitVisit(node);

        Dispatch(rule => rule.OnLeaveCreateOrAlterViewStatement(node, this));
    }

    public sealed override void ExplicitVisit(CreateTableStatement node)
    {
        Dispatch(rule => rule.OnEnterCreateTableStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(CreateExternalTableStatement node)
    {
        Dispatch(rule => rule.OnEnterCreateExternalTableStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(CreateIndexStatement node)
    {
        Dispatch(rule => rule.OnEnterCreateIndexStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(TopRowFilter node)
    {
        Dispatch(rule => rule.OnEnterTopRowFilter(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(OffsetClause node)
    {
        Dispatch(rule => rule.OnEnterOffsetClause(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(SelectScalarExpression node)
    {
        Dispatch(rule => rule.OnEnterSelectScalarExpression(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(OrderByClause node)
    {
        Dispatch(rule => rule.OnEnterOrderByClause(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(GroupByClause node)
    {
        Dispatch(rule => rule.OnEnterGroupByClause(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(FunctionCall node)
    {
        Dispatch(rule => rule.OnEnterFunctionCall(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(LeftFunctionCall node)
    {
        Dispatch(rule => rule.OnEnterLeftFunctionCall(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(RightFunctionCall node)
    {
        Dispatch(rule => rule.OnEnterRightFunctionCall(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(NamedTableReference node)
    {
        Dispatch(rule => rule.OnEnterNamedTableReference(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(UnpivotedTableReference node)
    {
        Dispatch(rule => rule.OnEnterUnpivotedTableReference(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(GlobalFunctionTableReference node)
    {
        Dispatch(rule => rule.OnEnterGlobalFunctionTableReference(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(SemanticTableReference node)
    {
        Dispatch(rule => rule.OnEnterSemanticTableReference(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(OutputClause node)
    {
        Dispatch(rule => rule.OnEnterOutputClause(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(ConvertCall node)
    {
        Dispatch(rule => rule.OnEnterConvertCall(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(CastCall node)
    {
        Dispatch(rule => rule.OnEnterCastCall(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(TryConvertCall node)
    {
        Dispatch(rule => rule.OnEnterTryConvertCall(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(TryCastCall node)
    {
        Dispatch(rule => rule.OnEnterTryCastCall(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(FetchCursorStatement node)
    {
        Dispatch(rule => rule.OnEnterFetchCursorStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(OpenCursorStatement node)
    {
        Dispatch(rule => rule.OnEnterOpenCursorStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(CloseCursorStatement node)
    {
        Dispatch(rule => rule.OnEnterCloseCursorStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(DeallocateCursorStatement node)
    {
        Dispatch(rule => rule.OnEnterDeallocateCursorStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(PrintStatement node)
    {
        Dispatch(rule => rule.OnEnterPrintStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(TableHint node)
    {
        Dispatch(rule => rule.OnEnterTableHint(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(ReadTextStatement node)
    {
        Dispatch(rule => rule.OnEnterReadTextStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(WriteTextStatement node)
    {
        Dispatch(rule => rule.OnEnterWriteTextStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(UpdateTextStatement node)
    {
        Dispatch(rule => rule.OnEnterUpdateTextStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(SetTransactionIsolationLevelStatement node)
    {
        Dispatch(rule => rule.OnEnterSetTransactionIsolationLevelStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(GlobalVariableExpression node)
    {
        Dispatch(rule => rule.OnEnterGlobalVariableExpression(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(GoToStatement node)
    {
        Dispatch(rule => rule.OnEnterGoToStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(ExecutableProcedureReference node)
    {
        Dispatch(rule => rule.OnEnterExecutableProcedureReference(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(BeginEndBlockStatement node)
    {
        Dispatch(rule => rule.OnEnterBeginEndBlockStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(ParenthesisExpression node)
    {
        Dispatch(rule => rule.OnEnterParenthesisExpression(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(BooleanParenthesisExpression node)
    {
        Dispatch(rule => rule.OnEnterBooleanParenthesisExpression(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(ExecuteStatement node)
    {
        Dispatch(rule => rule.OnEnterExecuteStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(SetCommandStatement node)
    {
        Dispatch(rule => rule.OnEnterSetCommandStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(OverClause node)
    {
        Dispatch(rule => rule.OnEnterOverClause(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(BeginTransactionStatement node)
    {
        Dispatch(rule => rule.OnEnterBeginTransactionStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(CommitTransactionStatement node)
    {
        Dispatch(rule => rule.OnEnterCommitTransactionStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(RollbackTransactionStatement node)
    {
        Dispatch(rule => rule.OnEnterRollbackTransactionStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(SaveTransactionStatement node)
    {
        Dispatch(rule => rule.OnEnterSaveTransactionStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(WaitForStatement node)
    {
        Dispatch(rule => rule.OnEnterWaitForStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(SetRowCountStatement node)
    {
        Dispatch(rule => rule.OnEnterSetRowCountStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(RevertStatement node)
    {
        Dispatch(rule => rule.OnEnterRevertStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(BackupDatabaseStatement node)
    {
        Dispatch(rule => rule.OnEnterBackupDatabaseStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(RestoreStatement node)
    {
        Dispatch(rule => rule.OnEnterRestoreStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(CreateDatabaseStatement node)
    {
        Dispatch(rule => rule.OnEnterCreateDatabaseStatement(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(StringLiteral node)
    {
        Dispatch(rule => rule.OnEnterStringLiteral(node, this));

        base.ExplicitVisit(node);
    }

    public sealed override void ExplicitVisit(BooleanComparisonExpression node)
    {
        Dispatch(rule => rule.OnEnterBooleanComparisonExpressionScope(node, this));

        base.ExplicitVisit(node);

        Dispatch(rule => rule.OnLeaveBooleanComparisonExpressionScope(node, this));
    }

    public override void Visit(BooleanComparisonExpression node)
    {
        Dispatch(rule => rule.OnBooleanComparisonExpression(node, this));

        base.Visit(node);
    }

    public override void Visit(BooleanTernaryExpression node)
    {
        Dispatch(rule => rule.OnBooleanTernaryExpression(node, this));

        base.Visit(node);
    }

    public override void Visit(LikePredicate node)
    {
        Dispatch(rule => rule.OnLikePredicate(node, this));

        base.Visit(node);
    }

    public override void Visit(InPredicate node)
    {
        Dispatch(rule => rule.OnInPredicate(node, this));

        base.Visit(node);
    }

    public override void Visit(SubqueryComparisonPredicate node)
    {
        Dispatch(rule => rule.OnSubqueryComparisonPredicate(node, this));

        base.Visit(node);
    }

    private void VisitProcedureOrFunctionBody(ProcedureStatementBodyBase node, SchemaObjectName name)
    {
        var previousScope = CurrentProcScope;
        CurrentProcScope = SchemaObjectNameHelper.Qualify(name);

        Dispatch(rule => rule.OnEnterProcedureOrFunctionBody(node, this));

        node.AcceptChildren(this);
        CurrentProcScope = previousScope;

        Dispatch(rule => rule.OnLeaveProcedureOrFunctionBody(node, this));
    }

    private void VisitTriggerBody(TriggerStatementBody node, SchemaObjectName name, TriggerObject triggerObject)
    {
        var previousScope = CurrentProcScope;
        CurrentProcScope = SchemaObjectNameHelper.Qualify(name);

        Dispatch(rule => rule.OnEnterTriggerBody(node, this));

        Dispatch(rule => rule.OnEnterTriggerStatementScope(node, name, triggerObject, this));

        if (triggerObject.Name is not { } targetTableName)
        {
            _ledger?.Record(
                _triggerScopeAnalysisPass, _sourcePath, node.StartLine, node.StartColumn,
                DdlOrLogonTriggerConstructKind, $"trigger scope '{triggerObject.TriggerScope}' has no target table - no inserted/deleted pseudo-tables to resolve");
            node.AcceptChildren(this);
            CurrentProcScope = previousScope;

            Dispatch(rule => rule.OnLeaveTriggerBody(node, this));

            return;
        }

        PushCteRelations(MergeCtes(CurrentCteRelations(), BuildTriggerPseudoTableRelations(targetTableName, node)));
        node.AcceptChildren(this);
        PopCteScope();

        CurrentProcScope = previousScope;

        Dispatch(rule => rule.OnLeaveTriggerBody(node, this));
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
