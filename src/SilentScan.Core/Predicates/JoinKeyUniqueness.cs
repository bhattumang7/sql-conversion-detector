using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

/// <summary>
/// "Which of a source relation's own columns does this join equate, and does a unique index prove
/// those columns identify at most one row?" - the rule that decides whether a multi-row join
/// source is a real hazard or a provably-safe one.
///
/// This is a SUPPRESSION rule, not a detection one: when
/// <see cref="IsProvenUniqueOver"/> returns true the caller returns without reporting. That makes
/// it precision-critical per CLAUDE.md - if two copies of it ever drift, one scanner starts
/// emitting findings another correctly suppresses, which is a false positive rather than a missed
/// one. It was duplicated verbatim between <c>NonUniqueUpdateSourceScanner</c> (UPDATE...FROM) and
/// <c>QueryAntiPatternScanner</c> (MERGE...USING), and the two copies had already begun to
/// diverge in their comments; extracting it makes the two statements provably share one answer.
///
/// Filtered and disabled indexes are excluded deliberately: a filtered index only constrains the
/// subset of rows matching its predicate, and a disabled one constrains nothing at all, so neither
/// proves uniqueness across the whole source.
/// </summary>
internal static class JoinKeyUniqueness
{
    /// <summary>
    /// The distinct columns of <paramref name="sourceAlias"/>'s own relation that
    /// <paramref name="searchCondition"/> constrains with an equality comparison. Matched by the
    /// join's own ALIAS rather than a resolved base-table name: a self-join aliases the identical
    /// table twice, so a qualified-name comparison could not tell one side's column from the
    /// other's. An empty result means nothing was resolvable back to that alias (a non-equality
    /// predicate, or a column shape this pass does not model) and the caller should leave the
    /// statement unanalyzed rather than guess.
    /// </summary>
    public static List<string> EqualityColumnsQualifiedBy(BooleanExpression? searchCondition, string sourceAlias) =>
        PredicateTreeWalker.FlattenAnd(searchCondition)
            .OfType<BooleanComparisonExpression>()
            .Where(c => c.ComparisonType == BooleanComparisonType.Equals)
            .SelectMany(c => new[] { c.FirstExpression, c.SecondExpression })
            .Select(e => DirectBaseTableResolver.ColumnNameIfQualifiedByAlias(e, sourceAlias))
            .Where(c => c is not null)
            .Select(c => c!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Whether <paramref name="table"/> carries a unique index whose every key column is among
    /// <paramref name="joinColumns"/> - i.e. the join keys provably match at most one source row.
    /// </summary>
    public static bool IsProvenUniqueOver(CatalogTable table, IReadOnlyCollection<string> joinColumns) =>
        table.Indexes.Any(ix =>
            ix.IsUnique && !ix.IsFiltered && !ix.IsDisabled
            && ix.KeyColumns.Count > 0
            && ix.KeyColumns.All(kc => joinColumns.Contains(kc, StringComparer.OrdinalIgnoreCase)));
}
