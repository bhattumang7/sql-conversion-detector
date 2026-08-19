using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;

namespace SilentScan.Core.Predicates;

/// <summary>
/// One statement's resolved shape, as the index-shape scanners need to see it: which real base
/// tables it reads, which of their columns an AND-reachable equality genuinely constrains, and the
/// join/WHERE fragments the constraint set was derived from (kept so a subclass can re-walk them
/// for its own, broader column-collection question).
/// </summary>
/// <param name="BaseTables">Every direct base table in scope - views, TVFs and derived tables are excluded, never guessed at.</param>
/// <param name="AndConstrainedColumns">
/// Columns reachable without crossing an OR. Deliberately narrow: a column only bound inside an OR
/// branch does not guarantee the engine is ever actually handed a value for it.
/// </param>
/// <param name="ScopeChain">The resolved alias scope, for a subclass that needs to resolve further column references itself.</param>
/// <param name="JoinNodes">The statement's flattened join nodes.</param>
/// <param name="WhereCondition">The statement's WHERE search condition, if any.</param>
/// <param name="Node">The statement fragment a finding should be reported against.</param>
internal sealed record ConstrainedStatement(
    IReadOnlyList<CatalogTable> BaseTables,
    HashSet<(string Table, string Column)> AndConstrainedColumns,
    IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> ScopeChain,
    IReadOnlyList<QualifiedJoin> JoinNodes,
    BooleanExpression? WhereCondition,
    TSqlFragment Node);

/// <summary>
/// The FROM-scope resolution shared by the index-shape scanners: visit every
/// SELECT/UPDATE/DELETE, resolve its FROM clause to real base tables, and work out which of their
/// columns the statement genuinely constrains - then hand that to
/// <see cref="InspectStatement"/> for the subclass's own rule.
///
/// <see cref="CompositeIndexLeadingColumnScanner"/> and <see cref="IndexCoverageScanner"/> carried
/// this block byte-for-byte, and IndexCoverageScanner's own doc comment already described itself as
/// sharing the other "almost verbatim" without either extracting it. Both rules need to see every
/// predicate touching a table before they can conclude anything about a specific column, which is
/// why neither folds into <see cref="TypedPredicateExtractor"/>'s one-comparison-at-a-time walk;
/// what differs between them is only which columns they then collect and what they conclude, so
/// that is all a subclass supplies.
/// </summary>
internal abstract class ConstrainedColumnStatementVisitor(string sourcePath, DatabaseCatalog catalog) : TSqlFragmentVisitor
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    protected string SourcePath { get; } = sourcePath;

    protected DatabaseCatalog Catalog { get; } = catalog;

    /// <summary>
    /// The enclosing SELECT's own CTE scope - a QuerySpecification has no direct access to its
    /// enclosing SelectStatement's WithCtesAndXmlNamespaces, so this is captured on the way down
    /// and consulted from <see cref="Inspect(FromClause?, BooleanExpression?, TSqlFragment)"/>.
    /// A CTE name shadows a same-named base table for the statement's own lifetime - resolving
    /// against the catalog instead (the previous, always-null cteRelations behavior) silently
    /// bound a CTE reference to an unrelated real table of the same name (2026-08 audit).
    /// </summary>
    private readonly Stack<IReadOnlyDictionary<string, ResolvedRelation>> cteScopeStack = new();

    /// <summary>Runs the subclass's rule over one resolved statement. Called once per SELECT/UPDATE/DELETE that resolves to at least one base table.</summary>
    protected abstract void InspectStatement(ConstrainedStatement statement);

    public override void ExplicitVisit(SelectStatement node)
    {
        cteScopeStack.Push(CteResolver.Resolve(node.WithCtesAndXmlNamespaces, Catalog, EmptyResolvedViews, SourcePath, ledger: null));
        base.ExplicitVisit(node);
        cteScopeStack.Pop();
    }

    public override void ExplicitVisit(QuerySpecification node)
    {
        Inspect(node.FromClause, node.WhereClause?.SearchCondition, node);
        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(UpdateStatement node)
    {
        var spec = node.UpdateSpecification;
        var cteRelations = CteResolver.Resolve(node.WithCtesAndXmlNamespaces, Catalog, EmptyResolvedViews, SourcePath, ledger: null);
        var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext(cteRelations));
        Inspect(byAlias, ordered, spec.FromClause, spec.WhereClause?.SearchCondition, node);
        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(DeleteStatement node)
    {
        var spec = node.DeleteSpecification;
        var cteRelations = CteResolver.Resolve(node.WithCtesAndXmlNamespaces, Catalog, EmptyResolvedViews, SourcePath, ledger: null);
        var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext(cteRelations));
        Inspect(byAlias, ordered, spec.FromClause, spec.WhereClause?.SearchCondition, node);
        base.ExplicitVisit(node);
    }

    private FromScopeResolver.ResolutionContext ResolutionContext(IReadOnlyDictionary<string, ResolvedRelation> cteRelations) =>
        new(Catalog, EmptyResolvedViews, SourcePath, Ledger: null, cteRelations, ProcScope: null);

    private void Inspect(FromClause? fromClause, BooleanExpression? whereCondition, TSqlFragment node)
    {
        if (fromClause is null)
        {
            return;
        }

        var cteRelations = cteScopeStack.Count > 0 ? cteScopeStack.Peek() : EmptyResolvedViews;
        var (byAlias, ordered) = FromScopeResolver.Resolve(fromClause, ResolutionContext(cteRelations));
        Inspect(byAlias, ordered, fromClause, whereCondition, node);
    }

    private void Inspect(
        IReadOnlyDictionary<string, ScopeEntry> byAlias, IReadOnlyList<ScopeEntry> ordered,
        FromClause? fromClause, BooleanExpression? whereCondition, TSqlFragment node)
    {
        var baseTables = ordered
            .Where(e => !e.IsViewLayer && e.Relation.QualifiedName is not null)
            .Select(e => e.Relation.QualifiedName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
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

        // AND-constrained: reachable without crossing an OR - a column only bound inside an
        // OR branch doesn't guarantee the leading column is ever actually supplied.
        var andConstrainedColumns = joinNodes
            .SelectMany(j => PredicateTreeWalker.FlattenAnd(j.SearchCondition))
            .Concat(PredicateTreeWalker.FlattenAnd(whereCondition))
            .OfType<BooleanComparisonExpression>()
            .SelectMany(c => BaseColumnResolver.ResolveBothSides(c, SourcePath, scopeChain))
            .ToHashSet(TableColumnKeyComparer.Instance);

        InspectStatement(new ConstrainedStatement(
            baseTables, andConstrainedColumns, scopeChain, joinNodes, whereCondition, node));
    }
}
