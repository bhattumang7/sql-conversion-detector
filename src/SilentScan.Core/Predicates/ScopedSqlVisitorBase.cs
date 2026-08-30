global using ScopeChain = System.Collections.Generic.IReadOnlyList<(
    System.Collections.Generic.IReadOnlyDictionary<string, SilentScan.Core.Lineage.ScopeEntry> ByAlias,
    System.Collections.Generic.IReadOnlyList<SilentScan.Core.Lineage.ScopeEntry> Ordered)>;

using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Predicates.Normalization;

namespace SilentScan.Core.Predicates;

#pragma warning disable CS9107
internal abstract class ScopedSqlVisitorBase(
    string sourcePath,
    DatabaseCatalog catalog,
    IReadOnlyDictionary<string, ResolvedRelation> resolvedViews,
    SkipLedger? ledger,
    string? currentProcScope,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? callerScopeByCalleeScope)
    : ScopedRelationWalker(sourcePath, catalog, resolvedViews, ledger, currentProcScope, callerScopeByCalleeScope)
#pragma warning restore CS9107
{
    protected const string NormalizationEliminatedConstructKind = "predicate eliminated by normalization";

    protected const string NormalizationEliminatedLedgerReason =
        "this comparison lives inside a branch the engine's own normalize/simplify pass proves can never contribute a selected row (a same-column contradiction, or a tautology on a confirmed NOT NULL column) - never reaches a real Filter/Seek decision";

    private static readonly IReadOnlySet<TSqlFragment> EmptyDeadPredicateSet = new HashSet<TSqlFragment>();

    protected readonly Stack<(Dictionary<string, ScopeEntry> ByAlias, List<ScopeEntry> Ordered)> ScopeStack = new();

    protected readonly Stack<IReadOnlySet<TSqlFragment>> DeadPredicateStack = new();

    protected override AnalysisPass TriggerScopeAnalysisPass => AnalysisPass.Predicates;

    protected bool IsDeadPredicate(TSqlFragment node) => DeadPredicateStack.Count > 0 && DeadPredicateStack.Peek().Contains(node);

    protected FromScopeResolver.ResolutionContext CurrentResolutionContext() =>
        new(catalog, resolvedViews, sourcePath, ledger, CurrentCteRelations(), CurrentProcScope, callerScopeByCalleeScope);

    protected ScopeChain CurrentScopeChain() =>
        ScopeStack.Select(s => ((IReadOnlyDictionary<string, ScopeEntry>)s.ByAlias, (IReadOnlyList<ScopeEntry>)s.Ordered)).ToList();

    protected IReadOnlySet<TSqlFragment> ComputeDeadPredicates(BooleanExpression? searchCondition)
    {
        if (searchCondition is null || ScopeStack.Count == 0)
        {
            return EmptyDeadPredicateSet;
        }

        var scopeChain = CurrentScopeChain();
        return PredicateSurvivalAnalyzer.FindDeadComparisons(searchCondition, columnRef => ResolveColumnFacts(columnRef, scopeChain));
    }

    protected PredicateSurvivalAnalyzer.ColumnFacts ResolveColumnFacts(ColumnReferenceExpression columnRef, ScopeChain scopeChain)
    {
        if (ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, ledger: null, catalog) is not ColumnProvenance.BaseColumn baseColumn)
        {
            return default;
        }

        var catalogColumn = catalog.Find(baseColumn.TableQualifiedName, CurrentProcScope)?.FindColumn(baseColumn.ColumnName, catalog.IdentifierComparer);
        return new PredicateSurvivalAnalyzer.ColumnFacts(
            catalogColumn is null || baseColumn.IsNullableSide ? null : !catalogColumn.IsNullable,
            baseColumn.Type?.Collation?.GuaranteesDistinctLiteralsAreUnequal);
    }

    protected void WithPredicateLocation(BooleanExpression? searchCondition, Action visitChildren)
    {
        DeadPredicateStack.Push(ComputeDeadPredicates(searchCondition));
        visitChildren();
        DeadPredicateStack.Pop();
    }

    protected static void InspectJoinOnClauses(
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

    protected static void InspectAllPredicateLocations(
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

    protected static void InspectAllPredicateLocations(
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

    protected static void InspectAllPredicateLocations(
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

    public sealed override void ExplicitVisit(SelectStatement node) =>
        WithCteScope(node.WithCtesAndXmlNamespaces, () =>
            OnSelectStatementScope(node, () => base.ExplicitVisit(node)));

    protected virtual void OnSelectStatementScope(SelectStatement node, Action continueDescent) =>
        continueDescent();

    public sealed override void ExplicitVisit(QuerySpecification node) =>
        WithFromScope(node.FromClause, () =>
            OnQuerySpecificationScope(node, CurrentScopeChain(), () => base.ExplicitVisit(node)));

    protected virtual void OnQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, Action continueDescent) =>
        continueDescent();

    public sealed override void ExplicitVisit(UpdateStatement node)
    {
        var spec = node.UpdateSpecification;
        WithCteScope(node.WithCtesAndXmlNamespaces, () =>
            WithDataModificationScope(spec.Target, spec.FromClause, () =>
                OnUpdateStatementScope(node, CurrentScopeChain(), () => base.ExplicitVisit(node))));
    }

    protected virtual void OnUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, Action continueDescent) =>
        continueDescent();

    public sealed override void ExplicitVisit(DeleteStatement node)
    {
        var spec = node.DeleteSpecification;
        WithCteScope(node.WithCtesAndXmlNamespaces, () =>
            WithDataModificationScope(spec.Target, spec.FromClause, () =>
                OnDeleteStatementScope(node, CurrentScopeChain(), () => base.ExplicitVisit(node))));
    }

    protected virtual void OnDeleteStatementScope(DeleteStatement node, ScopeChain scopeChain, Action continueDescent) =>
        continueDescent();

    public sealed override void ExplicitVisit(MergeStatement node)
    {
        var spec = node.MergeSpecification;
        WithCteScope(node.WithCtesAndXmlNamespaces, () =>
            WithMergeScope(spec.Target, spec.TableAlias, spec.TableReference, () =>
                OnMergeStatementScope(node, CurrentScopeChain(), () => base.ExplicitVisit(node))));
    }

    protected virtual void OnMergeStatementScope(MergeStatement node, ScopeChain scopeChain, Action continueDescent) =>
        continueDescent();

    public sealed override void ExplicitVisit(InsertStatement node) =>
        WithCteScope(node.WithCtesAndXmlNamespaces, () =>
            OnInsertStatementScope(node, () => base.ExplicitVisit(node)));

    protected virtual void OnInsertStatementScope(InsertStatement node, Action continueDescent) =>
        continueDescent();

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
        ScopeStack.Push(FromScopeResolver.Resolve(fromClause, CurrentResolutionContext()));
        try
        {
            body();
        }
        finally
        {
            ScopeStack.Pop();
        }
    }

    private void WithDataModificationScope(TableReference target, FromClause? extraFromClause, Action body)
    {
        ScopeStack.Push(FromScopeResolver.ResolveForDataModification(target, extraFromClause, CurrentResolutionContext()));
        try
        {
            body();
        }
        finally
        {
            ScopeStack.Pop();
        }
    }

    private void WithMergeScope(TableReference target, Identifier? targetAlias, TableReference source, Action body)
    {
        ScopeStack.Push(FromScopeResolver.ResolveForMerge(target, targetAlias, source, CurrentResolutionContext()));
        try
        {
            body();
        }
        finally
        {
            ScopeStack.Pop();
        }
    }
}
