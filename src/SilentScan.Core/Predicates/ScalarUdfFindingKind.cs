namespace SilentScan.Core.Predicates;

/// <summary>
/// How a scalar UDF's cost is being reached (docs/detection-checklist.md Tier 1 #1). Declaration
/// order is the report rank, matching <see cref="TvfFenceFindingKind"/>'s convention.
/// </summary>
public enum ScalarUdfFindingKind
{
    /// <summary>A predicate-region call (WHERE/JOIN ON/HAVING/MERGE ON) - non-sargable AND per-row AND (pre-2019 or non-inlineable) serial. The maximal claim, ranked first.</summary>
    PredicateInvocation,

    /// <summary>Reached through view/inline-TVF expansion - the 603-iTVF headline case, depth + origin like <see cref="TvfFenceFindingKind.NestedUnderViewOrTvf"/>.</summary>
    NestedUnderViewOrTvf,

    /// <summary>A computed column, DEFAULT, or CHECK constraint definition calls the UDF - catalog-only, poisons every query touching the table regardless of whether any query names the column.</summary>
    SchemaDependency,

    /// <summary>Every other scalar-expression context (SELECT list, ORDER BY, GROUP BY, SET, variable assignment) - per-row + serial but sargability is unaffected, so ranked last.</summary>
    ProjectionInvocation,
}
