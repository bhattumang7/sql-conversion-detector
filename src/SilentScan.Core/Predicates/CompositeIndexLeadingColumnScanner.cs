using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Hint and index-shape catalog checks": "Composite index
/// leading-column violation" - see <see cref="CompositeIndexLeadingColumnFinding"/> for the full
/// mechanism. Own standalone scanner, the same "spans the whole statement's FROM/WHERE/ON, not a
/// single comparison" reasoning <see cref="PartialCompositeForeignKeyJoinScanner"/>/<see
/// cref="CatchAllPredicateScanner"/> already document for why this isn't folded into <see
/// cref="TypedPredicateExtractor"/>'s one-comparison-at-a-time walk - this rule needs to see every
/// predicate touching a table before it can say a specific column was never bound anywhere.
/// </summary>
public static class CompositeIndexLeadingColumnScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<CompositeIndexLeadingColumnFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var visitor = new Visitor(parseResult.SourcePath, catalog);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column)
                .ThenBy(f => f.IndexName, StringComparer.OrdinalIgnoreCase),
        ];
    }

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog) : TSqlFragmentVisitor
    {
        public List<CompositeIndexLeadingColumnFinding> Findings { get; } = [];

        public override void ExplicitVisit(QuerySpecification node)
        {
            Inspect(node.FromClause, node.WhereClause?.SearchCondition, node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            var spec = node.UpdateSpecification;
            var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext());
            Inspect(byAlias, ordered, spec.FromClause, spec.WhereClause?.SearchCondition, node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            var spec = node.DeleteSpecification;
            var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext());
            Inspect(byAlias, ordered, spec.FromClause, spec.WhereClause?.SearchCondition, node);
            base.ExplicitVisit(node);
        }

        private FromScopeResolver.ResolutionContext ResolutionContext() =>
            new(catalog, EmptyResolvedViews, sourcePath, Ledger: null, CteRelations: null, ProcScope: null);

        private void Inspect(FromClause? fromClause, BooleanExpression? whereCondition, TSqlFragment node)
        {
            if (fromClause is null)
            {
                return;
            }

            var (byAlias, ordered) = FromScopeResolver.Resolve(fromClause, catalog, EmptyResolvedViews, sourcePath, ledger: null, cteRelations: null, procScope: null);
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
                .Select(name => catalog.Find(name))
                .Where(t => t is not null && t.Kind == CatalogTableKind.Table)
                .Select(t => t!)
                .ToList();

            if (baseTables.Count == 0)
            {
                return;
            }

            var scopeChain = new List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> { (byAlias, ordered) };

            var joinNodes = fromClause is null ? [] : fromClause.TableReferences.SelectMany(FlattenJoinNodes).ToList();

            // AND-constrained: reachable without crossing an OR - the same discipline
            // PartialCompositeForeignKeyJoinScanner's own FlattenAnd already applies, since a
            // column only bound inside an OR branch doesn't guarantee the leading column is ever
            // actually supplied.
            var andConstrainedColumns = joinNodes
                .SelectMany(j => FlattenAnd(j.SearchCondition))
                .Concat(FlattenAnd(whereCondition))
                .OfType<BooleanComparisonExpression>()
                .SelectMany(c => ResolveBothSides(c, scopeChain))
                .ToHashSet();

            // Referenced anywhere at all - deliberately broader than the AND-constrained set
            // (includes OR branches, IS NULL checks, every comparison operator) - used only to
            // suppress a violation, never to trigger one, so being liberal here is the safe
            // direction: a leading column referenced ANYWHERE, even weakly, is enough to decline.
            var anyReferencedColumns = new HashSet<(string Table, string Column)>();
            var referenceVisitor = new ColumnReferenceCollector(sourcePath, scopeChain, anyReferencedColumns);
            whereCondition?.Accept(referenceVisitor);
            foreach (var join in joinNodes)
            {
                join.SearchCondition.Accept(referenceVisitor);
            }

            foreach (var table in baseTables)
            {
                InspectTable(table, andConstrainedColumns, anyReferencedColumns, node);
            }
        }

        private void InspectTable(
            CatalogTable table,
            HashSet<(string Table, string Column)> andConstrainedColumns,
            HashSet<(string Table, string Column)> anyReferencedColumns,
            TSqlFragment node)
        {
            var usableIndexes = table.Indexes.Where(i => !i.IsFiltered && !i.IsColumnstore && !i.IsDisabled && i.KeyColumns.Count > 0).ToList();

            foreach (var index in usableIndexes.Where(i => i.KeyColumns.Count >= 2))
            {
                var leadingColumn = index.KeyColumns[0];
                if (anyReferencedColumns.Contains((table.QualifiedName, leadingColumn)))
                {
                    // Leading column is bound (or at least referenced) somewhere in the statement -
                    // this index has a real starting point, not a violation.
                    continue;
                }

                for (var position = 1; position < index.KeyColumns.Count; position++)
                {
                    var violatingColumn = index.KeyColumns[position];
                    if (!andConstrainedColumns.Contains((table.QualifiedName, violatingColumn)))
                    {
                        continue;
                    }

                    // Precision guard: only fire when no OTHER usable index on this table leads
                    // with the SAME violating column - if one does, the query has a real seekable
                    // path via that index regardless of this one's own shape, so flagging this
                    // index specifically would be index-shape noise, not a genuine "cannot seek
                    // anywhere" claim.
                    var hasAlternativeSeekPath = usableIndexes.Any(other =>
                        !ReferenceEquals(other, index)
                        && string.Equals(other.KeyColumns[0], violatingColumn, StringComparison.OrdinalIgnoreCase));
                    if (hasAlternativeSeekPath)
                    {
                        continue;
                    }

                    Findings.Add(new CompositeIndexLeadingColumnFinding(
                        table.QualifiedName, index.Name, index.KeyColumns, violatingColumn, position,
                        sourcePath, node.StartLine, node.StartColumn));
                    break; // one finding per index - the earliest violating column is evidence enough.
                }
            }
        }

        private IEnumerable<(string Table, string Column)> ResolveBothSides(
            BooleanComparisonExpression predicate,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            foreach (var side in new[] { predicate.FirstExpression, predicate.SecondExpression })
            {
                if (ResolveBaseColumn(side, scopeChain) is { } resolved)
                {
                    yield return resolved;
                }
            }
        }

        private (string Table, string Column)? ResolveBaseColumn(
            ScalarExpression expression,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            if (expression is not ColumnReferenceExpression columnRef)
            {
                return null;
            }

            var provenance = ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, ledger: null);
            return provenance is ColumnProvenance.BaseColumn { Depth: 0 } baseColumn
                ? (baseColumn.TableQualifiedName, baseColumn.ColumnName)
                : null;
        }

        /// <summary>Yields every <see cref="QualifiedJoin"/> node in the join tree - each carries its own ON clause.</summary>
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

        /// <summary>AND-flattens a search condition, never descending through OR - see this class's own doc comment.</summary>
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

        /// <summary>Collects every base-column reference reachable anywhere under a boolean expression, OR branches included - deliberately liberal, since this set is only ever used to suppress a finding, never to trigger one.</summary>
        private sealed class ColumnReferenceCollector(
            string sourcePath,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain,
            HashSet<(string Table, string Column)> sink) : TSqlFragmentVisitor
        {
            public override void ExplicitVisit(ColumnReferenceExpression node)
            {
                // A wildcard reference (bare * in SELECT *, or COUNT(*)'s own single argument) has
                // no MultiPartIdentifier at all - ResolveColumnReference assumes a real column name
                // is present and crashes on this shape, oracle-found against real corpus text (a
                // COUNT(*) nested inside a scalar subquery's own WHERE clause). Nothing to resolve
                // here regardless - a wildcard is never a specific column reference.
                if (node.ColumnType != ColumnType.Wildcard)
                {
                    var provenance = ScalarExpressionResolver.ResolveColumnReference(node, scopeChain, sourcePath, ledger: null);
                    if (provenance is ColumnProvenance.BaseColumn { Depth: 0 } baseColumn)
                    {
                        sink.Add((baseColumn.TableQualifiedName, baseColumn.ColumnName));
                    }
                }

                base.ExplicitVisit(node);
            }
        }
    }
}
