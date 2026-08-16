namespace SilentScan.Core.Predicates;

public enum ModuleCompileFlagFindingKind
{
    /// <summary>
    /// The module was authored <c>WITH RECOMPILE</c> (<c>sys.sql_modules.is_recompiled</c>) -
    /// every execution compiles a fresh plan and discards it immediately rather than caching it,
    /// so the module's own cost never accumulates in the plan cache at all
    /// (<c>sys.dm_exec_cached_plans</c>/<c>sys.dm_exec_query_stats</c>), invisible to any
    /// monitoring that reads those DMVs. docs/detection-checklist.md "Small precise adds".
    /// </summary>
    RecompilesEveryCall,

    /// <summary>
    /// A multi-statement table-valued function's own <c>RETURNS @t TABLE(...)</c> declares a
    /// character-typed column with no explicit <c>COLLATE</c> clause, so that column's collation
    /// was implicitly resolved against the CURRENT database's default collation at CREATE/ALTER
    /// time and baked in (<c>sys.sql_modules.uses_database_collation</c>) - a later <c>ALTER
    /// DATABASE ... COLLATE</c> changes what the database's default collation IS without the
    /// function's own already-compiled return shape ever being told, so the function's real,
    /// returned string collation silently disagrees with the (now different) database default.
    /// docs/detection-checklist.md "Small precise adds".
    /// </summary>
    TableValuedFunctionReturnUsesDatabaseCollation,
}

/// <summary>
/// Two independent <c>sys.sql_modules</c> catalog flags, each baked in wholesale at CREATE/ALTER
/// compile time (a mid-body statement has no bearing on either) - the same shape as <see
/// cref="SetOptionFinding"/>'s own catalog-flag half. <see cref="Line"/>/<see cref="Column"/>
/// point at the module's own CREATE/ALTER statement, matching <see
/// cref="SetOptionFindingKind.QuotedIdentifierOffBlocksIndexedFeature"/>'s identical precedent -
/// there is no in-body statement to point at for either kind.
///
/// <b><see cref="ModuleCompileFlagFindingKind.TableValuedFunctionReturnUsesDatabaseCollation"/>'s
/// scope, oracle-confirmed the hard way (Docker instance, 2026-08-17), not assumed from
/// documentation:</b> <c>uses_database_collation</c> is set to 1 for EVERY schema-bound object
/// (view/function) unconditionally, even one with zero string columns anywhere in its body or
/// return shape (confirmed directly: a pure-arithmetic <c>WITH SCHEMABINDING</c> scalar function
/// with an <c>INT</c> parameter and <c>INT</c> return still sets the flag) - schema-binding's own
/// identifier-resolution mechanism depends on the database's collation for case-insensitive name
/// matching regardless of data type, so the flag carries NO differentiating signal for a
/// schema-bound object: it is definitionally always 1 there. This finding therefore deliberately
/// EXCLUDES every schema-bound module (<c>is_schema_bound = 1</c>) from its fire condition -
/// reporting on that case would be a redundant, always-true, zero-information claim, not a real
/// risk report. What remains after that exclusion is a narrower, genuinely informative signal,
/// isolated and confirmed directly: a NON-schema-bound multi-statement table-valued function whose
/// own <c>RETURNS @t TABLE(...)</c> declares an un-<c>COLLATE</c>'d character column (confirmed:
/// flips to 0 the instant an explicit <c>COLLATE</c> clause is added to that same return column;
/// stays 0 for an INT-only return table with no strings at all). This is the one shape the flag
/// genuinely, uniquely reports on.
/// </summary>
public sealed record ModuleCompileFlagFinding(
    ModuleCompileFlagFindingKind Kind,
    string ModuleQualifiedName,
    string SourcePath,
    int Line,
    int Column,
    FindingConfidence Confidence = FindingConfidence.High);
