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

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog)
        : ConstrainedColumnStatementVisitor(sourcePath, catalog)
    {
        public List<CompositeIndexLeadingColumnFinding> Findings { get; } = [];

        protected override void InspectStatement(ConstrainedStatement statement)
        {
            // Referenced anywhere at all - deliberately broader than the AND-constrained set
            // (includes OR branches, IS NULL checks, every comparison operator) - used only to
            // suppress a violation, never to trigger one, so being liberal here is the safe
            // direction: a leading column referenced ANYWHERE, even weakly, is enough to decline.
            var anyReferencedColumns = new HashSet<(string Table, string Column)>(TableColumnKeyComparer.Instance);
            var referenceVisitor = new BaseColumnResolver.ColumnReferenceCollector(SourcePath, statement.ScopeChain, anyReferencedColumns);
            statement.WhereCondition?.Accept(referenceVisitor);
            foreach (var join in statement.JoinNodes)
            {
                join.SearchCondition.Accept(referenceVisitor);
            }

            foreach (var table in statement.BaseTables)
            {
                InspectTable(table, statement.AndConstrainedColumns, anyReferencedColumns, statement.Node);
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
                        SourcePath, node.StartLine, node.StartColumn));
                    break; // one finding per index - the earliest violating column is evidence enough.
                }
            }
        }
    }
}
