using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

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
