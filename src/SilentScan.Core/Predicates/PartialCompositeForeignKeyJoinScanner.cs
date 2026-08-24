using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates.Normalization;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class PartialCompositeForeignKeyJoinScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public sealed record CompositeForeignKey(
        string ConstraintName,
        string ParentTableQualifiedName,
        string ReferencedTableQualifiedName,
        IReadOnlyList<ForeignKeyColumnPair> Pairs);

    public static IReadOnlyList<CompositeForeignKey> BuildCompositeForeignKeys(DatabaseCatalog catalog) =>
        [.. catalog.ForeignKeys
            .GroupBy(fk => fk.ConstraintName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .Select(g => new CompositeForeignKey(
                g.Key,
                g.First().ParentTableQualifiedName,
                g.First().ReferencedTableQualifiedName,
                [.. g.Select(fk => new ForeignKeyColumnPair(fk.ParentColumnName, fk.ReferencedColumnName))]))];

    public static IReadOnlyList<PartialCompositeForeignKeyJoinFinding> Scan(
        SqlParseResult parseResult, DatabaseCatalog catalog, IReadOnlyList<CompositeForeignKey> compositeForeignKeys)
    {
        if (compositeForeignKeys.Count == 0)
        {
            return [];
        }

        var visitor = new Visitor(parseResult.SourcePath, catalog, compositeForeignKeys);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

#pragma warning disable CS9107
    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog, IReadOnlyList<CompositeForeignKey> compositeForeignKeys)
        : ScopedSqlVisitorBase(sourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null)
#pragma warning restore CS9107
    {
        public List<PartialCompositeForeignKeyJoinFinding> Findings { get; } = [];

        public override void ExplicitVisit(SelectStatement node)
        {
            PushCteScope(node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
            PopCteScope();
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            InspectFromClause(node.FromClause, node.WhereClause, CurrentCteRelations());
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            var cteRelations = CteResolver.Resolve(node.WithCtesAndXmlNamespaces, catalog, EmptyResolvedViews, sourcePath, ledger: null);
            InspectFromClause(node.UpdateSpecification.FromClause, node.UpdateSpecification.WhereClause, cteRelations);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            var cteRelations = CteResolver.Resolve(node.WithCtesAndXmlNamespaces, catalog, EmptyResolvedViews, sourcePath, ledger: null);
            InspectFromClause(node.DeleteSpecification.FromClause, node.DeleteSpecification.WhereClause, cteRelations);
            base.ExplicitVisit(node);
        }

        private void InspectFromClause(FromClause? fromClause, WhereClause? whereClause, IReadOnlyDictionary<string, ResolvedRelation> cteRelations)
        {
            if (fromClause is null)
            {
                return;
            }

            var (byAlias, ordered) = FromScopeResolver.Resolve(fromClause, catalog, EmptyResolvedViews, sourcePath, ledger: null, cteRelations, procScope: null);
            if (byAlias.Count == 0)
            {
                return;
            }

            var scopeChain = new List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)>
            {
                (byAlias, ordered),
            };

            if (PredicateSurvivalAnalyzer.IsUnsatisfiable(whereClause?.SearchCondition, columnRef => PredicateVisitorSupport.ResolveColumnFacts(columnRef, scopeChain, sourcePath, catalog)))
            {
                return;
            }

            var joinNodes = fromClause.TableReferences.SelectMany(PredicateTreeWalker.FlattenJoinNodes).ToList();
            if (joinNodes.Count == 0 && fromClause.TableReferences.Count < 2)
            {
                return;
            }

            var statementWideEqualities = joinNodes
                .SelectMany(j => PredicateTreeWalker.FlattenAnd(j.SearchCondition))
                .Concat(PredicateTreeWalker.FlattenAnd(whereClause?.SearchCondition))
                .OfType<BooleanComparisonExpression>()
                .Where(c => c.ComparisonType == BooleanComparisonType.Equals)
                .ToList();

            var directlyJoinedTablePairs = new HashSet<(string, string)>(TableColumnKeyComparer.Instance);
            foreach (var join in joinNodes)
            {
                InspectJoin(join, byAlias, scopeChain, statementWideEqualities, directlyJoinedTablePairs);
            }

            InspectCommaJoins(ordered, statementWideEqualities, scopeChain, directlyJoinedTablePairs, fromClause);
        }

        private void InspectJoin(
            QualifiedJoin join,
            IReadOnlyDictionary<string, ScopeEntry> byAlias,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
            IReadOnlyList<BooleanComparisonExpression> statementWideEqualities,
            HashSet<(string, string)> directlyJoinedTablePairs)
        {

            var firstTable = ResolveDirectBaseTable(join.FirstTableReference, byAlias);
            var secondTable = ResolveDirectBaseTable(join.SecondTableReference, byAlias);
            if (firstTable is null || secondTable is null)
            {
                return;
            }

            directlyJoinedTablePairs.Add(NormalizedPair(firstTable, secondTable));

            var joinEqualities = PredicateTreeWalker.FlattenAnd(join.SearchCondition)
                .OfType<BooleanComparisonExpression>()
                .Where(c => c.ComparisonType == BooleanComparisonType.Equals)
                .ToList();

            foreach (var fk in FindCandidateForeignKeys(firstTable, secondTable))
            {
                TryReportFinding(fk, joinEqualities, statementWideEqualities, scopeChain, join.StartLine, join.StartColumn);
            }
        }

        private void InspectCommaJoins(
            IReadOnlyList<ScopeEntry> ordered,
            IReadOnlyList<BooleanComparisonExpression> statementWideEqualities,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
            HashSet<(string, string)> directlyJoinedTablePairs,
            FromClause fromClause)
        {
            var baseTables = ordered
                .Where(e => !e.IsViewLayer)
                .Select(e => e.Relation.QualifiedName)
                .Where(name => name is not null && catalog.Find(name)?.Kind == CatalogTableKind.Table)
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var i = 0; i < baseTables.Count; i++)
            {
                for (var j = i + 1; j < baseTables.Count; j++)
                {
                    var pairKey = NormalizedPair(baseTables[i], baseTables[j]);
                    if (directlyJoinedTablePairs.Contains(pairKey))
                    {
                        continue;
                    }

                    foreach (var fk in FindCandidateForeignKeys(baseTables[i], baseTables[j]))
                    {

                        TryReportFinding(fk, statementWideEqualities, statementWideEqualities, scopeChain, fromClause.StartLine, fromClause.StartColumn);
                    }
                }
            }
        }

        private void TryReportFinding(
            CompositeForeignKey fk,
            IReadOnlyList<BooleanComparisonExpression> localEqualities,
            IReadOnlyList<BooleanComparisonExpression> statementWideEqualities,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
            int line, int column)
        {
            var coveredLocally = fk.Pairs.Where(p => localEqualities.Any(pred => PredicateCoversPair(pred, scopeChain, fk, p))).ToList();
            if (coveredLocally.Count == 0)
            {

                return;
            }

            var coveredAnywhere = fk.Pairs.Where(p => statementWideEqualities.Any(pred => PredicateCoversPair(pred, scopeChain, fk, p))).ToList();
            var missingEverywhere = fk.Pairs.Except(coveredAnywhere).ToList();
            if (missingEverywhere.Count == 0)
            {

                return;
            }

            if (IsSuppressedByUniqueIndex(fk, coveredLocally))
            {
                return;
            }

            Findings.Add(new PartialCompositeForeignKeyJoinFinding(
                fk.ConstraintName, fk.ParentTableQualifiedName, fk.ReferencedTableQualifiedName,
                fk.Pairs, coveredLocally, missingEverywhere, sourcePath, line, column));
        }

        private bool IsSuppressedByUniqueIndex(CompositeForeignKey fk, IReadOnlyList<ForeignKeyColumnPair> coveredLocally)
        {
            var referencedTable = catalog.Find(fk.ReferencedTableQualifiedName);
            if (referencedTable is null)
            {
                return false;
            }

            var usedReferencedColumns = new HashSet<string>(coveredLocally.Select(p => p.ReferencedColumnName), StringComparer.OrdinalIgnoreCase);
            return referencedTable.Indexes.Any(i => i.IsUnique && i.KeyColumns.Count > 0 && i.KeyColumns.All(usedReferencedColumns.Contains));
        }

        private bool PredicateCoversPair(
            BooleanComparisonExpression predicate,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
            CompositeForeignKey fk, ForeignKeyColumnPair pair)
        {
            var left = BaseColumnResolver.ResolveBaseColumn(predicate.FirstExpression, sourcePath, scopeChain);
            var right = BaseColumnResolver.ResolveBaseColumn(predicate.SecondExpression, sourcePath, scopeChain);
            if (left is null || right is null)
            {
                return false;
            }

            return (Matches(left, fk.ParentTableQualifiedName, pair.ParentColumnName) && Matches(right, fk.ReferencedTableQualifiedName, pair.ReferencedColumnName))
                || (Matches(left, fk.ReferencedTableQualifiedName, pair.ReferencedColumnName) && Matches(right, fk.ParentTableQualifiedName, pair.ParentColumnName));
        }

        private static bool Matches(ColumnProvenance.BaseColumn? resolved, string table, string column) =>
            resolved is { } r
            && string.Equals(r.TableQualifiedName, table, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.ColumnName, column, StringComparison.OrdinalIgnoreCase);

        private string? ResolveDirectBaseTable(TableReference tableReference, IReadOnlyDictionary<string, ScopeEntry> byAlias)
        {
            if (tableReference is not NamedTableReference named)
            {
                return null;
            }

            var alias = named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value;
            return byAlias.TryGetValue(alias, out var entry)
                && !entry.IsViewLayer
                && entry.Relation.QualifiedName is { } qualifiedName
                && catalog.Find(qualifiedName) is { Kind: CatalogTableKind.Table }
                ? qualifiedName
                : null;
        }

        private IEnumerable<CompositeForeignKey> FindCandidateForeignKeys(string tableA, string tableB) =>
            compositeForeignKeys.Where(fk =>
                (Eq(fk.ParentTableQualifiedName, tableA) && Eq(fk.ReferencedTableQualifiedName, tableB))
                || (Eq(fk.ParentTableQualifiedName, tableB) && Eq(fk.ReferencedTableQualifiedName, tableA)));

        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static (string, string) NormalizedPair(string a, string b) =>
            string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0 ? (a, b) : (b, a);

    }
}
