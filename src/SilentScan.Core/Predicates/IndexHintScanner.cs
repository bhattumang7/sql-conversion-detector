using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class IndexHintScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<IndexHintFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = new Rule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column)
                .ThenBy(f => f.HintedIndexName, StringComparer.OrdinalIgnoreCase),
        ];
    }

    private sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        public List<IndexHintFinding> Findings { get; } = [];

        public void OnEnterQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, ModuleWalker walker) =>
            Inspect(node.FromClause, node.WhereClause?.SearchCondition, walker.CurrentCteRelations());

        public void OnEnterUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, ModuleWalker walker)
        {
            var spec = node.UpdateSpecification;
            var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext(walker));
            InspectResolved(byAlias, ordered, spec.FromClause, spec.WhereClause?.SearchCondition, spec.Target);
        }

        public void OnEnterDeleteStatementScope(DeleteStatement node, ScopeChain scopeChain, ModuleWalker walker)
        {
            var spec = node.DeleteSpecification;
            var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext(walker));
            InspectResolved(byAlias, ordered, spec.FromClause, spec.WhereClause?.SearchCondition, spec.Target);
        }

        private FromScopeResolver.ResolutionContext ResolutionContext(ModuleWalker walker) =>
            new(catalog, EmptyResolvedViews, sourcePath, Ledger: null, walker.CurrentCteRelations(), ProcScope: null);

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

            var anyReferencedColumns = new HashSet<(string Table, string Column)>(TableColumnKeyComparer.For(catalog));
            var referenceVisitor = new BaseColumnResolver.ColumnReferenceCollector(sourcePath, scopeChain, anyReferencedColumns, catalog);
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

                var matchedIndex = table.Indexes.FirstOrDefault(i => catalog.IdentifierComparer.Equals(i.Name, hintedName));
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
