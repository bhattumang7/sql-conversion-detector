namespace SilentScan.Core.Predicates;

/// <param name="Kind">Which syntactic non-sargable pattern fired.</param>
/// <param name="ColumnName">The bare (unqualified) column name the pattern was found on.</param>
/// <param name="Detail">Pattern-specific extra context (the function name, the CAST/CONVERT keyword, the arithmetic operator) - null when the pattern kind carries none.</param>
/// <param name="SourcePath">Where this finding's own predicate lives.</param>
/// <param name="Line">1-based line of the finding's own predicate.</param>
/// <param name="Column">1-based column of the finding's own predicate.</param>
/// <param name="DynamicSqlCallSite">Set when this finding was found inside a reparsed dynamic SQL script - where the EXEC/sp_executesql call site lives, distinct from <paramref name="SourcePath"/>/<paramref name="Line"/> (the finding's true source line inside the folded string).</param>
/// <param name="TableQualifiedName">
/// The base table this column resolved to through the catalog/lineage (however many view
/// layers deep), or null when the column couldn't be resolved to a real catalog table at all
/// (a cross-file reference the DDL for which was never scanned, an alias that doesn't resolve,
/// a column on a CTE/derived table with no traceable base column, ...). Absence here is
/// informational, not a claim the column doesn't exist.
/// </param>
/// <param name="Indexed">
/// True only if <paramref name="TableQualifiedName"/> resolved AND that column is the LEADING
/// key column of a genuinely seekable index (<see cref="Catalog.CatalogTable.IsIndexedColumn"/>)
/// - a syntactic non-sargable pattern on an unindexed column costs nothing extra beyond the
/// pattern itself (there was no seek to lose). False means resolved-but-not-indexed (a known,
/// confident answer). Null means "could not resolve the column at all" - CLAUDE.md's "never
/// guess" discipline applied to this field specifically: null is NOT the same claim as false,
/// and must never be presented to a reader as "not indexed."
/// </param>
/// <param name="PredicateFragmentText">
/// Roadmap Phase E3: the exact fragment <see cref="NonSargablePredicateScanner"/> matched on,
/// re-rendered to valid T-SQL text via <see cref="Rules.FragmentTextRenderer"/> - for
/// <see cref="SargabilityFindingKind.LeadingWildcardLike"/>/<see
/// cref="SargabilityFindingKind.LikePatternNotLiteral"/> this is the WHOLE LIKE predicate
/// (already a complete, probeable boolean expression); for the other three kinds it is the bare
/// wrapping scalar expression (e.g. <c>UPPER(Code)</c>), which a probe still has to wrap in its
/// own synthesized comparison. Lets the corpus oracle actually probe this finding instead of
/// merely trusting the classifier that detected it - previously this had no oracle path at all.
/// </param>
public sealed record SargabilityFinding(
    SargabilityFindingKind Kind,
    string ColumnName,
    string? Detail,
    string SourcePath,
    int Line,
    int Column,
    SourceSpan? DynamicSqlCallSite = null,
    string? TableQualifiedName = null,
    bool? Indexed = null,
    string? PredicateFragmentText = null);
