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

        public override void ExplicitVisit(QuerySpecification node)
        {
            InspectFromClause(node.FromClause, node.WhereClause);
            base.ExplicitVisit(node);
        }

        // UPDATE ... FROM / DELETE ... FROM can join exactly like a SELECT's own FROM clause -
        // the row-multiplication risk is identical (more rows deleted/updated than the caller
        // intended), so both are in scope alongside the ordinary SELECT case.
        public override void ExplicitVisit(UpdateStatement node)
        {
            InspectFromClause(node.UpdateSpecification.FromClause, node.UpdateSpecification.WhereClause);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            InspectFromClause(node.DeleteSpecification.FromClause, node.DeleteSpecification.WhereClause);
            base.ExplicitVisit(node);
        }

        private void InspectFromClause(FromClause? fromClause, WhereClause? whereClause)
        {
            if (fromClause is null)
            {
                return;
            }

            var (byAlias, ordered) = FromScopeResolver.Resolve(fromClause, catalog, EmptyResolvedViews, sourcePath, ledger: null, cteRelations: null, procScope: null);
            if (byAlias.Count == 0)
            {
                return;
            }

            var scopeChain = new List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)>
            {
                (byAlias, ordered),
            };

            var joinNodes = fromClause.TableReferences.SelectMany(FlattenJoinNodes).ToList();
            if (joinNodes.Count == 0 && fromClause.TableReferences.Count < 2)
            {
                return;
            }

            // Every equality predicate anywhere in this statement's own join predicates or WHERE -
            // the pool a "missing" pair is checked against before firing, so a composite key split
            // across a JOIN's ON and a WHERE-clause filter (a real, common pattern) is never
            // misread as a bug.
            var statementWideEqualities = joinNodes
                .SelectMany(j => FlattenAnd(j.SearchCondition))
                .Concat(FlattenAnd(whereClause?.SearchCondition))
                .OfType<BooleanComparisonExpression>()
                .Where(c => c.ComparisonType == BooleanComparisonType.Equals)
                .ToList();

            var directlyJoinedTablePairs = new HashSet<(string, string)>();
            foreach (var join in joinNodes)
            {
                InspectJoin(join, scopeChain, statementWideEqualities, directlyJoinedTablePairs);
            }

            InspectCommaJoins(ordered, statementWideEqualities, scopeChain, directlyJoinedTablePairs, fromClause);
        }

        /// <summary>Yields every <see cref="QualifiedJoin"/> node in the join tree, innermost/outermost alike - each one independently checked, since each carries its own ON clause.</summary>
        private static IEnumerable<QualifiedJoin> FlattenJoinNodes(TableReference tableReference)
        {
            switch (tableReference)
            {
                case QualifiedJoin join:
                    foreach (var t in FlattenJoinNodes(join.FirstTableReference))
                    {
                        yield return t;
                    }

                    foreach (var t in FlattenJoinNodes(join.SecondTableReference))
                    {
                        yield return t;
                    }

                    yield return join;
                    break;

                case JoinParenthesisTableReference parenthesis:
                    foreach (var t in FlattenJoinNodes(parenthesis.Join))
                    {
                        yield return t;
                    }

                    break;
            }
        }

        private void InspectJoin(
            QualifiedJoin join,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
            IReadOnlyList<BooleanComparisonExpression> statementWideEqualities,
            HashSet<(string, string)> directlyJoinedTablePairs)
        {
            // Only a direct, single-table-to-single-table join is in scope for v1 - if either side
            // is itself a nested join (a 3+-way join chain), a view/derived table/CTE, or a temp
            // table/table variable (which can never have a real FK), this join is skipped rather
            // than guessed about. A real FK can only ever exist between two persisted base tables.
            var firstTable = ResolveDirectBaseTable(join.FirstTableReference);
            var secondTable = ResolveDirectBaseTable(join.SecondTableReference);
            if (firstTable is null || secondTable is null)
            {
                return;
            }

            directlyJoinedTablePairs.Add(NormalizedPair(firstTable, secondTable));

            var joinEqualities = FlattenAnd(join.SearchCondition)
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
            var left = ResolveBaseColumn(predicate.FirstExpression, scopeChain);
            var right = ResolveBaseColumn(predicate.SecondExpression, scopeChain);
            if (left is null || right is null)
            {
                return false;
            }

            return (Matches(left, fk.ParentTableQualifiedName, pair.ParentColumnName) && Matches(right, fk.ReferencedTableQualifiedName, pair.ReferencedColumnName))
                || (Matches(left, fk.ReferencedTableQualifiedName, pair.ReferencedColumnName) && Matches(right, fk.ParentTableQualifiedName, pair.ParentColumnName));
        }

        private static bool Matches((string Table, string Column)? resolved, string table, string column) =>
            resolved is { } r
            && string.Equals(r.Table, table, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Column, column, StringComparison.OrdinalIgnoreCase);

        private (string Table, string Column)? ResolveBaseColumn(
            ScalarExpression expression,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            if (expression is not ColumnReferenceExpression columnRef)
            {
                return null;
            }

            // Depth == 0 required: a column reached through a view layer is not a direct base-
            // table predicate this scanner's base-table-only scope covers (see the finding's own
            // doc comment). The ledger is null throughout this pass, since NonSargablePredicateScanner
            // and TypedPredicateExtractor already run full coverage reporting over the same files
            // and this scanner's own unresolved references would just be duplicate noise.
            var provenance = ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, ledger: null);
            return provenance is ColumnProvenance.BaseColumn { Depth: 0 } baseColumn
                ? (baseColumn.TableQualifiedName, baseColumn.ColumnName)
                : null;
        }

        private string? ResolveDirectBaseTable(TableReference tableReference)
        {
            if (tableReference is not NamedTableReference named)
            {
                return null;
            }

            var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(named.SchemaObject));
            return catalog.Find(qualifiedName) is { Kind: CatalogTableKind.Table } ? qualifiedName : null;
        }

        private IEnumerable<CompositeForeignKey> FindCandidateForeignKeys(string tableA, string tableB) =>
            compositeForeignKeys.Where(fk =>
                (Eq(fk.ParentTableQualifiedName, tableA) && Eq(fk.ReferencedTableQualifiedName, tableB))
                || (Eq(fk.ParentTableQualifiedName, tableB) && Eq(fk.ReferencedTableQualifiedName, tableA)));

        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static (string, string) NormalizedPair(string a, string b) =>
            string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

        /// <summary>AND-flattens a search condition, never descending through OR - a column pair only reachable through an OR branch doesn't guarantee the equality holds, so it must never count as "covered".</summary>
        private static IEnumerable<BooleanExpression> FlattenAnd(BooleanExpression? expression)
        {
            switch (expression)
            {
                case null:
                    yield break;

                case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } and:
                    foreach (var e in FlattenAnd(and.FirstExpression))
                    {
                        yield return e;
                    }

                    foreach (var e in FlattenAnd(and.SecondExpression))
                    {
                        yield return e;
                    }

                    break;

                case BooleanParenthesisExpression paren:
                    foreach (var e in FlattenAnd(paren.Expression))
                    {
                        yield return e;
                    }

                    break;

                default:
                    yield return expression;
                    break;
            }
        }
    }
}
