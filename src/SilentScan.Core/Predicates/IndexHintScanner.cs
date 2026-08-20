using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Hint and index-shape catalog checks": "Hint validity against the
/// catalog" - see <see cref="IndexHintFinding"/>/<see cref="IndexHintFindingKind"/> for the
/// mechanism behind each kind. Own standalone scanner: needs the same whole-statement predicate
/// visibility <see cref="CompositeIndexLeadingColumnScanner"/> needs (is the hinted index's
/// leading column bound ANYWHERE in the statement, not just locally on the hinted table
/// reference itself), plus a direct per-table-reference AST walk neither
/// <see cref="TypedPredicateExtractor"/> nor that scanner performs (table hints live on the
/// <see cref="NamedTableReference"/> node itself, not inside a predicate).
/// </summary>
public static class IndexHintScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<IndexHintFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var visitor = new Visitor(parseResult.SourcePath, catalog);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column)
                .ThenBy(f => f.HintedIndexName, StringComparer.OrdinalIgnoreCase),
        ];
    }

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog) : TSqlFragmentVisitor
    {
        public List<IndexHintFinding> Findings { get; } = [];

        /// <summary>
        /// The enclosing SELECT's own CTE scope - a QuerySpecification has no direct access to
        /// its enclosing SelectStatement's WithCtesAndXmlNamespaces. A CTE is never schema-
        /// qualified, so it always shadows a same-named real base table; resolving through the
        /// catalog instead (cteRelations always null, pre-fix) silently matched a CTE-shadowed
        /// hinted table against an unrelated real table sharing its name (2026-08 audit).
        /// </summary>
        private readonly Stack<IReadOnlyDictionary<string, ResolvedRelation>> cteScopeStack = new();

        public override void ExplicitVisit(SelectStatement node)
        {
            cteScopeStack.Push(CteResolver.Resolve(node.WithCtesAndXmlNamespaces, catalog, EmptyResolvedViews, sourcePath, ledger: null));
            base.ExplicitVisit(node);
            cteScopeStack.Pop();
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            var cteRelations = cteScopeStack.Count > 0 ? cteScopeStack.Peek() : EmptyResolvedViews;
            Inspect(node.FromClause, node.WhereClause?.SearchCondition, cteRelations);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            var spec = node.UpdateSpecification;
            var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext(node.WithCtesAndXmlNamespaces));
            InspectResolved(byAlias, ordered, spec.FromClause, spec.WhereClause?.SearchCondition, spec.Target);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            var spec = node.DeleteSpecification;
            var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext(node.WithCtesAndXmlNamespaces));
            InspectResolved(byAlias, ordered, spec.FromClause, spec.WhereClause?.SearchCondition, spec.Target);
            base.ExplicitVisit(node);
        }

        private FromScopeResolver.ResolutionContext ResolutionContext(WithCtesAndXmlNamespaces? withClause) =>
            new(catalog, EmptyResolvedViews, sourcePath, Ledger: null, CteResolver.Resolve(withClause, catalog, EmptyResolvedViews, sourcePath, ledger: null), ProcScope: null);

        private void Inspect(FromClause? fromClause, BooleanExpression? whereCondition, IReadOnlyDictionary<string, ResolvedRelation> cteRelations)
        {
            if (fromClause is null)
            {
                return;
            }

            var (byAlias, ordered) = FromScopeResolver.Resolve(fromClause, catalog, EmptyResolvedViews, sourcePath, ledger: null, cteRelations, procScope: null);
            InspectResolved(byAlias, ordered, fromClause, whereCondition, target: null);
        }

        private void InspectResolved(
            IReadOnlyDictionary<string, ScopeEntry> byAlias, IReadOnlyList<ScopeEntry> ordered,
            FromClause? fromClause, BooleanExpression? whereCondition, TableReference? target)
        {
            var scopeChain = new List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> { (byAlias, ordered) };

            var joinNodes = fromClause is null ? [] : fromClause.TableReferences.SelectMany(PredicateTreeWalker.FlattenJoinNodes).ToList();

            var anyReferencedColumns = new HashSet<(string Table, string Column)>(TableColumnKeyComparer.Instance);
            var referenceVisitor = new BaseColumnResolver.ColumnReferenceCollector(sourcePath, scopeChain, anyReferencedColumns);
            whereCondition?.Accept(referenceVisitor);
            foreach (var join in joinNodes)
            {
                join.SearchCondition.Accept(referenceVisitor);
            }

            var namedTables = (fromClause is null ? [] : fromClause.TableReferences.SelectMany(PredicateTreeWalker.FlattenNamedTables))
                .Concat(target is NamedTableReference targetNamed ? [targetNamed] : [])
                .ToList();

            foreach (var namedTable in namedTables)
            {
                InspectNamedTable(namedTable, byAlias, anyReferencedColumns);
            }
        }

        /// <summary>
        /// Resolved through the ALREADY CTE-aware <paramref name="byAlias"/> scope (built by
        /// <see cref="FromScopeResolver"/>) rather than an independent
        /// <c>SchemaObjectNameHelper.Qualify</c> + <c>catalog.Find</c> lookup of its own - the
        /// independent lookup bypassed CTE shadowing entirely regardless of what cteRelations the
        /// caller resolved, since a CTE is never schema-qualified and a raw re-qualify-and-
        /// catalog-lookup can never see it (2026-08 audit, same shape as
        /// PartialCompositeForeignKeyJoinScanner's own ResolveDirectBaseTable fix).
        /// </summary>
        private void InspectNamedTable(NamedTableReference namedTable, IReadOnlyDictionary<string, ScopeEntry> byAlias, HashSet<(string Table, string Column)> anyReferencedColumns)
        {
            var indexHints = namedTable.TableHints.OfType<IndexTableHint>().ToList();
            if (indexHints.Count == 0)
            {
                return;
            }

            var alias = namedTable.Alias?.Value ?? namedTable.SchemaObject.BaseIdentifier.Value;
            if (!byAlias.TryGetValue(alias, out var entry) || entry.IsViewLayer || entry.Relation.QualifiedName is not { } qualifiedName)
            {
                return;
            }

            var table = catalog.Find(qualifiedName);
            if (table is null || table.Kind != CatalogTableKind.Table)
            {
                return;
            }

            foreach (var hint in indexHints)
            {
                InspectHint(hint, table, anyReferencedColumns);
            }
        }

        private void InspectHint(IndexTableHint hint, CatalogTable table, HashSet<(string Table, string Column)> anyReferencedColumns)
        {
            foreach (var indexValue in hint.IndexValues)
            {
                // Ordinal form (INDEX(0)/INDEX(1)) has no Identifier and no catalog name to
                // validate - deliberately out of v1 scope, see the finding's own doc comment.
                var hintedName = indexValue.Identifier?.Value;
                if (hintedName is null)
                {
                    continue;
                }

                var matchedIndex = table.Indexes.FirstOrDefault(i => string.Equals(i.Name, hintedName, StringComparison.OrdinalIgnoreCase));
                if (matchedIndex is null)
                {
                    Findings.Add(new IndexHintFinding(
                        IndexHintFindingKind.IndexDoesNotExist, table.QualifiedName, hintedName, LeadingColumnName: null,
                        sourcePath, hint.StartLine, hint.StartColumn));
                    continue;
                }

                if (matchedIndex.KeyColumns.Count == 0)
                {
                    continue;
                }

                var leadingColumn = matchedIndex.KeyColumns[0];
                if (!anyReferencedColumns.Contains((table.QualifiedName, leadingColumn)))
                {
                    Findings.Add(new IndexHintFinding(
                        IndexHintFindingKind.HintedIndexNotSeekable, table.QualifiedName, hintedName, leadingColumn,
                        sourcePath, hint.StartLine, hint.StartColumn));
                }
            }
        }

    }
}
