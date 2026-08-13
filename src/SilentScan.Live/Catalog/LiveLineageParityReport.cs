namespace SilentScan.Live.Catalog;

/// <summary>
/// The live lineage parity gate's four outcomes, replacing what used to be one flat mismatch
/// list. See <see cref="LiveLineageParityChecker"/> for why a plain cached-<c>sys.columns</c>
/// diff conflates three distinct situations for a view/inline TVF.
/// </summary>
public sealed record LiveLineageParityReport(
    IReadOnlyList<LiveLineageParityMismatch> Mismatches,
    IReadOnlyList<LiveLineageStaleMetadata> StaleCachedMetadata,
    IReadOnlyList<LiveLineageUncompilableObject> UncompilableObjects,
    IReadOnlyList<LiveLineageUnverifiedColumn> Unverified)
{
    public static readonly LiveLineageParityReport Empty = new([], [], [], []);
}

/// <summary>
/// A genuine disagreement between what this tool inferred and ground truth: for a view/inline
/// TVF, the type the engine computes for that object right now; for a base table or
/// multi-statement TVF, its <c>sys.columns</c>/authored shape, which cannot go stale. This is the
/// only category that is a bug in this tool rather than a condition of the scanned database, and
/// the only one the live scan's exit code keys on.
/// </summary>
public sealed record LiveLineageParityMismatch(string QualifiedViewName, string ColumnName, string Facet, string InferredValue, string ActualValue);

/// <summary>
/// A view/inline-TVF column where this tool's inference agrees with what the engine computes for
/// it right now, but the object's cached <c>sys.columns</c> metadata disagrees with both - an
/// upstream base column was retyped after the view/function was created, and SQL Server never
/// refreshes a view's/function's own cached metadata on its own. A maintenance signal for whoever
/// owns the database (<c>sp_refreshview</c>/<c>sp_refreshsqlmodule</c>), not a bug in this tool.
/// </summary>
public sealed record LiveLineageStaleMetadata(string QualifiedViewName, string ColumnName, string Facet, string CachedValue, string LiveValue);

/// <summary>
/// A view/inline TVF the engine could not compile at all when asked to describe it (it
/// references something that no longer exists, most often). A condition of the scanned database,
/// not a bug in this tool - its cached metadata is a fossil from when it last compiled.
/// </summary>
public sealed record LiveLineageUncompilableObject(string QualifiedViewName, int ErrorNumber, string ErrorMessage);

/// <summary>
/// A column this gate could not get a live answer for - the object's live-described result set
/// didn't include it, or none of its parameters could be rendered as a typed <c>NULL</c> probe
/// argument (a table-valued parameter, or a type with no fixed T-SQL spelling) - reported only
/// when the fallback cached-metadata comparison also disagrees, so an honestly-uncheckable column
/// is never silently dropped, but a merely-unprobeable one that already agrees isn't noise either.
/// </summary>
public sealed record LiveLineageUnverifiedColumn(string QualifiedViewName, string ColumnName, string Reason, string InferredValue, string CachedValue);
