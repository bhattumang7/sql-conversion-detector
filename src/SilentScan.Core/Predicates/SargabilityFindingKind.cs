namespace SilentScan.Core.Predicates;

/// <summary>Tier-1 syntactic non-sargable predicate patterns (CLAUDE.md: "no types needed").</summary>
public enum SargabilityFindingKind
{
    /// <summary>YEAR(col) = ..., UPPER(col) = ..., ISNULL(col, x) = ...</summary>
    FunctionWrappedColumn,

    /// <summary>CONVERT(type, col) = ..., CAST(col AS type) = ...</summary>
    CastOrConvertOnColumn,

    /// <summary>col + 1 = ..., col * 2 &gt; ...</summary>
    ColumnArithmetic,

    /// <summary>col LIKE '%...'</summary>
    LeadingWildcardLike,

    /// <summary>col LIKE @p - the pattern isn't a literal, so a leading wildcard can't be ruled out statically.</summary>
    LikePatternNotLiteral,

    /// <summary>
    /// UPPER(col) = ... / LOWER(col) = ... - split out from the generic FunctionWrappedColumn
    /// kind because the finding's OWN remediation differs by the column's real collation
    /// (docs/detection-checklist.md Tier 1 "Type-aware upgrade of the sargability stream" #4):
    /// oracle-verified the wrap forces a scan under EITHER a case-sensitive OR a case-insensitive
    /// collation (SQL Server does not special-case away UPPER/LOWER for CI columns the way the
    /// checklist originally assumed), so this is never suppressed by collation - only the
    /// <see cref="SargabilityFinding.Detail"/> remediation text changes: a CI-collation column's
    /// wrap is a provably safe, zero-risk deletion (the wrap changes nothing about which rows
    /// match); a CS/BIN-collation column's wrap is load-bearing for correctness and needs a real
    /// rewrite (an indexed computed column, or COLLATE on the literal instead).
    /// </summary>
    CaseFoldOnColumn,

    /// <summary>
    /// YEAR(col)/MONTH(col)/DAY(col)/DATEPART(unit, col)/DATEDIFF(unit, col, x)/DATEADD(unit, n,
    /// col) wrapping a column in a predicate (docs/detection-checklist.md Tier 1 "Type-aware
    /// upgrade of the sargability stream" #2) - split out from the generic FunctionWrappedColumn
    /// kind for a named, higher-signal identity (real base-rate measurement: DATEDIFF alone is
    /// the single largest named opportunity in this whole stream, ahead of ISNULL/COALESCE).
    /// Oracle-verified structurally identical to case-folding: the wrap forces a scan regardless
    /// of comparison type (there is no verdict/collation question, only whether an indexed
    /// computed column matches - the same <see cref="ComputedColumnMatcher"/> precision guard
    /// shipped for JSON_VALUE and case-folding applies here unchanged).
    /// </summary>
    DateFunctionOnColumn,
}
