using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Catalog-only pass (docs/detection-checklist.md "Temporal table history-side index gap") - no
/// AST walking, mirrors <see cref="UntrustedConstraintScanner"/>'s own shape: every current-side
/// index lacking a structurally matching history-side index is flagged once, regardless of
/// whether any scanned query issues a <c>FOR SYSTEM_TIME</c> query against it. See
/// <see cref="TemporalTableHistoryIndexGapFinding"/> for the oracle-decided match criterion and
/// the PRIMARY KEY/UNIQUE exclusion.
/// </summary>
public static class TemporalTableHistoryIndexGapScanner
{
    public static IReadOnlyList<TemporalTableHistoryIndexGapFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<TemporalTableHistoryIndexGapFinding>();

        foreach (var pair in catalog.TemporalTablePairs)
        {
            var current = catalog.Find(pair.CurrentTableQualifiedName);
            var history = catalog.Find(pair.HistoryTableQualifiedName);
            if (current is null || history is null)
            {
                // Never guessed at - a temporal pairing sys.tables itself reported, but one side
                // wasn't independently resolved by the ordinary table/index read (should not
                // happen against a live target, since both are plain sys.tables rows, but this
                // pass never assumes a live-only registry and a live-only table read agree).
                continue;
            }

            foreach (var index in current.Indexes.Where(IsComparableIndex))
            {
                var hasMatch = history.Indexes.Any(h => IsComparableIndex(h) && SameKeyColumns(index, h));
                if (hasMatch)
                {
                    continue;
                }

                findings.Add(new TemporalTableHistoryIndexGapFinding(
                    current.QualifiedName, history.QualifiedName, index.Name, index.KeyColumns,
                    current.SourcePath, current.SourceLine));
            }
        }

        return
        [
            .. findings
                .OrderBy(f => f.CurrentTableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.CurrentIndexName, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// The same "genuinely seekable" eligibility <see cref="CatalogTable.IsIndexedColumn"/> uses,
    /// plus PRIMARY KEY/UNIQUE CONSTRAINT excluded - oracle-confirmed structurally impossible on a
    /// temporal history table (Msg 13558/13583), so comparing either would be an always-mismatched,
    /// unfixable signal rather than a real gap. See the finding's own doc comment for the oracle
    /// evidence.
    /// </summary>
    private static bool IsComparableIndex(CatalogIndex index) =>
        index.Kind == CatalogIndexKind.Index && !index.IsFiltered && !index.IsColumnstore && !index.IsDisabled
        && index.KeyColumns.Count > 0;

    /// <summary>Order-sensitive, case-insensitive key-column equality - see the finding's own doc comment for why order is deliberately significant here.</summary>
    private static bool SameKeyColumns(CatalogIndex current, CatalogIndex candidate) =>
        current.KeyColumns.SequenceEqual(candidate.KeyColumns, StringComparer.OrdinalIgnoreCase);
}
