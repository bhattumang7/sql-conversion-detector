namespace SilentScan.Core.Predicates.DynamicSqlValue;

/// <summary>
/// Optional live single-row text fetch capability for the dynamic-SQL engine's
/// SELECT-assignment-from-table splice (<c>DynamicSqlTransfer.TryCompileSelectAssignmentFromSingleKnownTable</c>).
/// Core itself makes no network calls (CLAUDE.md) - this is a pure interface with zero
/// implementations in this project; the only real implementation lives in <c>SilentScan.Live</c>,
/// wired in only for <c>scan-db</c> and only when the <c>--fetch-sql-from-tables</c> flag is
/// passed. Never used for corpus/file-mode scans (there is no live connection to fetch through).
/// </summary>
public interface ILiveRowValueFetcher
{
    /// <summary>
    /// Fetches up to <paramref name="maxRows"/> DISTINCT values of <paramref name="selectColumn"/>
    /// from <paramref name="tableQualifiedName"/>, filtered by whatever (Column, LiteralValue)
    /// pairs <paramref name="equalityKeys"/> supplies (AND'd) - which may be empty, meaning no
    /// filter is statically known and every distinct value in the column is a real candidate.
    /// Null when the read fails or zero rows match; a non-null, non-empty list otherwise (never
    /// more than <paramref name="maxRows"/> entries - the caller treats each as one independently
    /// analyzable candidate, so this is the same cardinality cap every other source of divergence
    /// in the dynamic-SQL engine already respects). Every implementation must issue a
    /// parameterized, SELECT-only query - the literal key values extracted from source text are
    /// untrusted input from the caller's own perspective and must never be string-interpolated
    /// into SQL text.
    /// </summary>
    IReadOnlyList<string>? TryFetchDistinctValues(
        string tableQualifiedName, string selectColumn, IReadOnlyList<(string Column, string LiteralValue)> equalityKeys, int maxRows);
}
