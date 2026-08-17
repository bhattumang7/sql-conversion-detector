namespace SilentScan.Core.Predicates;

public enum DatabaseConfigurationFindingKind
{
    /// <summary>PAGE_VERIFY is not CHECKSUM (TORN_PAGE_DETECTION or NONE) - silent storage-level
    /// page corruption goes undetected until a much later, harder-to-diagnose failure.</summary>
    PageVerifyNotChecksum,

    /// <summary>AUTO_SHRINK is ON - a well-known, severe anti-pattern: the engine repeatedly
    /// shrinks and the workload immediately re-grows the file, causing constant fragmentation
    /// churn for no durable space saving.</summary>
    AutoShrinkOn,

    /// <summary>AUTO_CLOSE is ON - the database's own connection/buffer-pool state is torn down
    /// after the last connection closes and rebuilt from scratch on the next one, adding real
    /// latency to whichever connection happens to be first.</summary>
    AutoCloseOn,

    /// <summary>TARGET_RECOVERY_TIME is 0 (disabled) - indirect checkpoint is off, falling back
    /// to the legacy automatic-checkpoint mechanism sized by RECOVERY INTERVAL rather than a
    /// bounded, predictable crash-recovery time.</summary>
    TargetRecoveryTimeUnset,

    /// <summary>Query Store is not actively running (actual state is not READ_WRITE) - the
    /// engine's own built-in plan-regression/history diagnostic is unavailable for this
    /// database. Reported informationally: unlike the other kinds here, "should Query Store be
    /// on" genuinely depends on workload and operational choice, not a universal anti-pattern.</summary>
    QueryStoreNotReadWrite,

    /// <summary>Query Store is running but its capture mode is not AUTO (e.g. ALL or NONE) -
    /// informational, the same workload-dependent reasoning as <see cref="QueryStoreNotReadWrite"/>:
    /// ALL is a deliberate, real choice some teams prefer for troubleshooting, not a mistake.</summary>
    QueryStoreCaptureModeNotAuto,

    /// <summary><c>sys.databases.is_auto_create_stats_on = 0</c> - the optimizer can no longer
    /// create a missing single-column statistics object on demand, so a predicate against an
    /// unstatted column compiles against a guessed cardinality instead of a real histogram. A
    /// long-established, essentially uncontroversial anti-pattern to have turned off.</summary>
    AutoCreateStatisticsOff,

    /// <summary><c>sys.databases.is_auto_update_stats_on = 0</c> - statistics never refresh as the
    /// underlying data changes, so every plan compiled against them drifts further from reality
    /// the longer the database runs. Same severity class as <see cref="AutoCreateStatisticsOff"/>.</summary>
    AutoUpdateStatisticsOff,

    /// <summary>
    /// <c>sys.databases.compatibility_level</c> for the target database is lower than the
    /// connected engine instance's own current default - see <see cref="DatabaseConfigurationFinding"/>'s
    /// own doc comment for how "the engine's own current default" is determined live rather than
    /// hardcoded from a version-number mapping. A database sitting behind that default is silently
    /// kept on an older cardinality estimator and query-optimizer behavior nobody chose on purpose;
    /// each compat-level jump is itself a real, sometimes plan-changing behavior shift, which is
    /// exactly why this is reported as a gap rather than a specific target level.
    /// </summary>
    CompatibilityLevelBehindEngineDefault,
}

/// <summary>
/// docs/detection-checklist.md "Second OSS/commercial sweep": "Database-level configuration
/// flags" - a genuinely new finding CATEGORY, not module/column/predicate-level like every other
/// stream in this codebase: reported once per SCAN RUN against the target database itself, read
/// directly from <c>sys.databases</c> (and, for the two Query Store kinds,
/// <c>sys.database_query_store_options</c>) - no query text involved at all. Live-mode only by
/// construction (there is no file-mode equivalent of "the database's own current configuration");
/// always empty from <see cref="Reporting.ScanReportBuilder"/>, merged in by
/// <c>LiveScanRunner</c> after a real live connection, the same pattern
/// <c>TempTableExecShapeFindings</c> already established for a live-only concern.
///
/// docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §A extends this same stream
/// with three more kinds rather than a new type (database-granularity facts belong with the other
/// database-granularity facts): <see cref="DatabaseConfigurationFindingKind.AutoCreateStatisticsOff"/>,
/// <see cref="DatabaseConfigurationFindingKind.AutoUpdateStatisticsOff"/> (both straight
/// <c>sys.databases</c> flags, read in the same single row already fetched for the original six),
/// and <see cref="DatabaseConfigurationFindingKind.CompatibilityLevelBehindEngineDefault"/>.
///
/// <b>How "the engine's own current default compat level" is determined</b>, for the last one:
/// NOT a <c>SERVERPROPERTY('ProductMajorVersion')</c>-derived version-number mapping (SQL Server
/// 2022 -> 160, 2025 -> a level this codebase would need to know about the day it ships, an Azure
/// SQL DB edition's own default history, a CU that changes the mapping - any of which silently
/// goes stale) but a live read of <c>compatibility_level</c> on the <c>model</c> system database
/// on the SAME connected instance, from the unqualified, server-scoped <c>sys.databases</c> catalog
/// view (no database-context switch needed - confirmed directly: <c>model</c>'s own row is visible
/// from any database's connection). <c>model</c> is what the engine itself clones every newly
/// created database from, so its compat level IS this specific engine instance's own current
/// default, verified once against the standing disposable Docker instance rather than assumed.
/// Robust to edition/version differences by construction, since it asks the engine instead of a
/// table baked into this codebase.
///
/// <b>Severity is NOT uniform across the flags</b> - deliberately, after checking current
/// engine defaults directly rather than assuming every flag is an equally-confident "always
/// should be X" claim:
/// <list type="bullet">
/// <item><see cref="DatabaseConfigurationFindingKind.PageVerifyNotChecksum"/>,
/// <see cref="DatabaseConfigurationFindingKind.AutoShrinkOn"/>,
/// <see cref="DatabaseConfigurationFindingKind.AutoCloseOn"/>: long-established, essentially
/// uncontroversial DBA anti-patterns - SARIF Warning.</item>
/// <item><see cref="DatabaseConfigurationFindingKind.TargetRecoveryTimeUnset"/>: confirmed
/// directly against a freshly created database on the same engine instance (the <c>model</c>
/// system database, which every new database is cloned from) that the engine's OWN modern
/// default is <c>target_recovery_time_in_seconds = 60</c>, not 0 - a database sitting at 0 has
/// deviated from that default, disabling indirect checkpoint entirely. SARIF Warning - a specific,
/// well-documented (Microsoft's own "Database Checkpoints" guidance since SQL Server 2016)
/// recommendation to enable it, not a workload judgment call.</item>
/// <item><see cref="DatabaseConfigurationFindingKind.QueryStoreNotReadWrite"/> and
/// <see cref="DatabaseConfigurationFindingKind.QueryStoreCaptureModeNotAuto"/>: reported at SARIF
/// Note only - unlike the flags above, Query Store's own actual/desired state and capture mode
/// are genuine, deliberate operational choices (ALL capture mode is a real, common choice for
/// active troubleshooting; some teams disable Query Store entirely on very high-churn ad-hoc
/// workloads to avoid its own overhead), not a universal anti-pattern the way AUTO_SHRINK is.
/// <see cref="DatabaseConfigurationFindingKind.QueryStoreCaptureModeNotAuto"/> is only even
/// evaluated when Query Store's own actual state IS READ_WRITE - reporting a capture-mode
/// complaint about a Query Store that isn't even running would be a confusing, redundant second
/// finding for the same underlying fact.</item>
/// <item><see cref="DatabaseConfigurationFindingKind.AutoCreateStatisticsOff"/> and
/// <see cref="DatabaseConfigurationFindingKind.AutoUpdateStatisticsOff"/>: SARIF Warning - the
/// same "long-established, essentially uncontroversial" class as the first three; both ON is the
/// engine's own out-of-the-box default and turning either off is a deliberate, rare, and usually
/// mistaken choice.</item>
/// <item><see cref="DatabaseConfigurationFindingKind.CompatibilityLevelBehindEngineDefault"/>:
/// SARIF Warning - being behind is unambiguous once "the engine's own current default" is known
/// precisely (see above), even though this finding deliberately does not claim a specific target
/// level is correct for this workload (a deliberate pin for a known regression is legitimate; the
/// gap itself, silently accumulated rather than chosen, is what this reports).</item>
/// </list>
///
/// No plan-XML oracle applies - every value here is a directly-read, exact catalog fact, not a
/// plan-shape or execution-behavior claim.
/// </summary>
public sealed record DatabaseConfigurationFinding(
    DatabaseConfigurationFindingKind Kind,
    string DatabaseName,
    FindingConfidence Confidence = FindingConfidence.High);
