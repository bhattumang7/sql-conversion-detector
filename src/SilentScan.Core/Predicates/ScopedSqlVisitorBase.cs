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

    protected static void InspectJoinOnClauses(
        IList<TableReference>? tableReferences,
        IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
        Action<BooleanExpression, IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)>> inspect)
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
}
