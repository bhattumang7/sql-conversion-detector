using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;

namespace SilentScan.Core.Predicates;

internal sealed record ConstrainedStatement(
    IReadOnlyList<CatalogTable> BaseTables,
    HashSet<ColumnProvenance.BaseColumn> AndConstrainedColumns,
    IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> ScopeChain,
    IReadOnlyList<QualifiedJoin> JoinNodes,
    BooleanExpression? WhereCondition,
    TSqlFragment Node);

#pragma warning disable CS9107
internal abstract class ConstrainedColumnStatementVisitor(string sourcePath, DatabaseCatalog catalog)
    : ScopedSqlVisitorBase(sourcePath, catalog, ConstrainedColumnStatementVisitor.EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null)
#pragma warning restore CS9107
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    protected string SourcePath { get; } = sourcePath;

    protected DatabaseCatalog Catalog { get; } = catalog;

    protected abstract void InspectStatement(ConstrainedStatement statement);

    protected override void OnQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, Action continueDescent)
    {
        Inspect(node.FromClause, node.WhereClause?.SearchCondition, node);
        continueDescent();
    }

    protected override void OnUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, Action continueDescent)
    {
        var spec = node.UpdateSpecification;
        var cteRelations = CteResolver.Resolve(node.WithCtesAndXmlNamespaces, Catalog, EmptyResolvedViews, SourcePath, ledger: null);
        var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext(cteRelations));
        Inspect(byAlias, ordered, spec.FromClause, spec.WhereClause?.SearchCondition, node);
        continueDescent();
    }

    protected override void OnDeleteStatementScope(DeleteStatement node, ScopeChain scopeChain, Action continueDescent)
    {
        var spec = node.DeleteSpecification;
        var cteRelations = CteResolver.Resolve(node.WithCtesAndXmlNamespaces, Catalog, EmptyResolvedViews, SourcePath, ledger: null);
        var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext(cteRelations));
        Inspect(byAlias, ordered, spec.FromClause, spec.WhereClause?.SearchCondition, node);
        continueDescent();
    }

    private FromScopeResolver.ResolutionContext ResolutionContext(IReadOnlyDictionary<string, ResolvedRelation> cteRelations) =>
        new(Catalog, EmptyResolvedViews, SourcePath, Ledger: null, cteRelations, ProcScope: null);

    private void Inspect(FromClause? fromClause, BooleanExpression? whereCondition, TSqlFragment node)
    {
        if (fromClause is null)
        {
            return;
        }

        var (byAlias, ordered) = FromScopeResolver.Resolve(fromClause, ResolutionContext(CurrentCteRelations()));
        Inspect(byAlias, ordered, fromClause, whereCondition, node);
    }

    private void Inspect(
        IReadOnlyDictionary<string, ScopeEntry> byAlias, IReadOnlyList<ScopeEntry> ordered,
        FromClause? fromClause, BooleanExpression? whereCondition, TSqlFragment node)
    {
        var baseTables = ordered
            .Where(e => !e.IsViewLayer && e.Relation.QualifiedName is not null)
            .Select(e => e.Relation.QualifiedName!)
            .Distinct(Catalog.IdentifierComparer)
            .Select(name => Catalog.Find(name))
            .Where(t => t is not null && t.Kind == CatalogTableKind.Table)
            .Select(t => t!)
            .ToList();

        if (baseTables.Count == 0)
        {
            return;
        }

        var scopeChain = new List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> { (byAlias, ordered) };
        var joinNodes = fromClause is null ? [] : fromClause.TableReferences.SelectMany(PredicateTreeWalker.FlattenJoinNodes).ToList();

        var andConstrainedColumns = joinNodes
            .SelectMany(j => PredicateTreeWalker.FlattenAnd(j.SearchCondition))
            .Concat(PredicateTreeWalker.FlattenAnd(whereCondition))
            .OfType<BooleanComparisonExpression>()
            .SelectMany(c => BaseColumnResolver.ResolveBothSides(c, SourcePath, scopeChain, Catalog))
            .ToHashSet(TableColumnKeyComparer.For(Catalog));

        InspectStatement(new ConstrainedStatement(
            baseTables, andConstrainedColumns, scopeChain, joinNodes, whereCondition, node));
    }
}
