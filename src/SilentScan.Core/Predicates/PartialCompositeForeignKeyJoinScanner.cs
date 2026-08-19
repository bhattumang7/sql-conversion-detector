using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 1 "Join predicate incomplete vs. the backing foreign key" -
/// a hybrid pass: catalog-only for FK discovery (<see cref="BuildCompositeForeignKeys"/>, live-
/// mode only, mirroring <see cref="CrossTableTypeDriftScanner"/>), but needs a real per-file AST
/// walk (unlike that scanner) to see which columns a JOIN's own ON clause - or a legacy comma
/// join's WHERE-clause condition - actually equates.
///
/// Deliberately excludes the "FK exists but the ON clause matches none of its columns" shape:
/// that's a different, much lower-precision claim ("you joined on the wrong column entirely" vs.
/// "you joined on the right key but forgot part of it") with real, legitimate T-SQL shapes behind
/// it (bridge tables, hierarchy self-joins, business-key joins) - see the finding's own doc
/// comment. Only fires when the join ALREADY equates at least one of the FK's column pairs and
/// still omits at least one other, uncovered anywhere else in the same statement (another JOIN's
/// ON, or the WHERE clause) - and even then, only when the omission can actually multiply rows:
/// if the column subset the join DOES use is itself covered by a unique index on the referenced
/// (parent) side, the match is still at-most-one-row regardless of what's missing, and this
/// scanner suppresses it.
/// </summary>
public static class PartialCompositeForeignKeyJoinScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    /// <summary>One real FK constraint's full column list, grouped from the flat per-pair <see cref="DatabaseCatalog.ForeignKeys"/> list - only composite (2+ pair) constraints are kept, since a single-column FK has nothing to be "partial" about.</summary>
    public sealed record CompositeForeignKey(
        string ConstraintName,
        string ParentTableQualifiedName,
        string ReferencedTableQualifiedName,
        IReadOnlyList<ForeignKeyColumnPair> Pairs);

    /// <summary>
    /// Hoisted out of the per-file scan loop by the caller (<see cref="Reporting.ScanReportBuilder"/>) -
    /// the same grouping otherwise recomputed on every one of a corpus's several thousand files.
    /// </summary>
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

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog, IReadOnlyList<CompositeForeignKey> compositeForeignKeys) : TSqlFragmentVisitor
    {
        public List<PartialCompositeForeignKeyJoinFinding> Findings { get; } = [];

        /// <summary>
        /// The enclosing SELECT's own CTE scope - a QuerySpecification has no direct access to
        /// its enclosing SelectStatement's WithCtesAndXmlNamespaces. A CTE is never schema-
        /// qualified, so it always shadows a same-named real base table; resolving through the
        /// catalog instead (cteRelations always null, pre-fix) silently matched a CTE-shadowed
        /// join side against an unrelated real table sharing its name, which could either
        /// fabricate a partial-composite-FK finding or produce a wrong TableQualifiedName on a
        /// real one (2026-08 audit).
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
            InspectFromClause(node.FromClause, node.WhereClause, cteRelations);
            base.ExplicitVisit(node);
        }

        // UPDATE ... FROM / DELETE ... FROM can join exactly like a SELECT's own FROM clause -
        // the row-multiplication risk is identical (more rows deleted/updated than the caller
        // intended), so both are in scope alongside the ordinary SELECT case.
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

            var joinNodes = fromClause.TableReferences.SelectMany(PredicateTreeWalker.FlattenJoinNodes).ToList();
            if (joinNodes.Count == 0 && fromClause.TableReferences.Count < 2)
            {
                return;
            }

            // Every equality predicate anywhere in this statement's own join predicates or WHERE -
            // the pool a "missing" pair is checked against before firing, so a composite key split
            // across a JOIN's ON and a WHERE-clause filter (a real, common pattern) is never
            // misread as a bug.
            var statementWideEqualities = joinNodes
                .SelectMany(j => PredicateTreeWalker.FlattenAnd(j.SearchCondition))
                .Concat(PredicateTreeWalker.FlattenAnd(whereClause?.SearchCondition))
                .OfType<BooleanComparisonExpression>()
                .Where(c => c.ComparisonType == BooleanComparisonType.Equals)
                .ToList();

            // ValueTuple element names are compile-time only, so a (string Table, string Column)
            // comparer is the same underlying type as (string, string) - reused directly rather
            // than duplicating it under a pair-specific name (2026-08 audit: this set and
            // NormalizedPair's own CompareOrdinal ordering both compared table names
            // case-sensitively, so a same-pair join spelled with different casing either missed
            // the "already directly joined" suppression or produced two distinct pair keys for
            // one real pair).
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
            // Only a direct, single-table-to-single-table join is in scope for v1 - if either side
            // is itself a nested join (a 3+-way join chain), a view/derived table/CTE, or a temp
            // table/table variable (which can never have a real FK), this join is skipped rather
            // than guessed about. A real FK can only ever exist between two persisted base tables.
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

        /// <summary>
        /// The legacy `FROM A, B WHERE A.x = B.y` shape has no ON clause of its own to inspect -
        /// every pair of base tables present in this FROM clause's flattened leaf list that both
        /// (a) shares a composite FK and (b) was NOT already inspected as a direct ANSI JOIN above
        /// is checked against the WHERE clause's own equality predicates instead. Row-multiplication
        /// risk is identical to the ANSI-JOIN case; only the syntax differs.
        /// </summary>
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
                        // No dedicated ON clause exists for a comma join - the WHERE-wide equality
                        // pool (already part of statementWideEqualities) is both the "local"
                        // coverage source and the "anywhere else" pool, since there is nowhere
                        // else for a comma join's own condition to live.
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
                // No overlap with this FK at all - "you didn't use the FK" is a different, lower-
                // precision claim this stream deliberately does not make.
                return;
            }

            var coveredAnywhere = fk.Pairs.Where(p => statementWideEqualities.Any(pred => PredicateCoversPair(pred, scopeChain, fk, p))).ToList();
            var missingEverywhere = fk.Pairs.Except(coveredAnywhere).ToList();
            if (missingEverywhere.Count == 0)
            {
                // Every pair is covered SOMEWHERE in the statement (possibly split across a JOIN's
                // ON and a WHERE-clause filter) - not a defect.
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

        /// <summary>
        /// If the column subset THIS join actually uses is itself a superset of some real unique
        /// index's key columns on the referenced (parent) side, the match can never multiply rows
        /// regardless of what the FK's remaining columns would have added - not a defect, even
        /// though it diverges from the FK's own full declared shape.
        /// </summary>
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

        /// <summary>
        /// Resolved through the ALREADY CTE-aware scope chain (<paramref name="byAlias"/>, built
        /// by <see cref="FromScopeResolver"/>) rather than an independent
        /// <c>SchemaObjectNameHelper.Qualify</c> + <c>catalog.Find</c> lookup of its own - the
        /// independent lookup bypassed CTE shadowing entirely regardless of what cteRelations
        /// <see cref="InspectFromClause"/> resolved, since a CTE is never schema-qualified and a
        /// raw re-qualify-and-catalog-lookup can never see it (2026-08 audit).
        /// </summary>
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
