using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Predicates.Normalization;

namespace SilentScan.Core.Predicates;

internal static class PredicateVisitorSupport
{
    public static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static PredicateSurvivalAnalyzer.ColumnFacts ResolveColumnFacts(
        ColumnReferenceExpression columnRef,
        IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
        string sourcePath,
        DatabaseCatalog catalog)
    {
        if (ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, ledger: null) is not ColumnProvenance.BaseColumn baseColumn)
        {
            return default;
        }

        var catalogColumn = catalog.Find(baseColumn.TableQualifiedName)?.FindColumn(baseColumn.ColumnName);
        return new PredicateSurvivalAnalyzer.ColumnFacts(
            catalogColumn is null ? null : !catalogColumn.IsNullable,
            baseColumn.Type?.Collation?.IsCaseSensitive);
    }

    public static FromScopeResolver.ResolutionContext ResolutionContext(WithCtesAndXmlNamespaces? withClause, string sourcePath, DatabaseCatalog catalog) =>
        new(catalog, EmptyResolvedViews, sourcePath, Ledger: null,
            CteResolver.Resolve(withClause, catalog, EmptyResolvedViews, sourcePath, ledger: null), ProcScope: null);

    public static FromScopeResolver.ResolutionContext ResolutionContext(
        IReadOnlyDictionary<string, ResolvedRelation> cteRelations, string sourcePath, DatabaseCatalog catalog) =>
        new(catalog, EmptyResolvedViews, sourcePath, Ledger: null, cteRelations, ProcScope: null);

    public static List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> ScopeChainOf(
        (IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered) resolved) => [resolved];

    public static void InspectJoinOnClauses(
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

internal sealed class CteScopeTracker(string sourcePath, DatabaseCatalog catalog)
{
    private readonly Stack<IReadOnlyDictionary<string, ResolvedRelation>> _stack = new();

    public IReadOnlyDictionary<string, ResolvedRelation> Current =>
        _stack.Count > 0 ? _stack.Peek() : PredicateVisitorSupport.EmptyResolvedViews;

    public void PushForSelect(WithCtesAndXmlNamespaces? withClause) =>
        _stack.Push(CteResolver.Resolve(withClause, catalog, PredicateVisitorSupport.EmptyResolvedViews, sourcePath, ledger: null));

    public void Pop() => _stack.Pop();
}
