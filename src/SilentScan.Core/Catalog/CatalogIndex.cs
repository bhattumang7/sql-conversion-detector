namespace SilentScan.Core.Catalog;

/// <summary>
/// <paramref name="IsFiltered"/>/<paramref name="IsColumnstore"/> exist so ranking can stop
/// treating these as a plain seekable index (docs/audit-remediation-plan.md Phase 2.5): a
/// filtered index only covers rows matching its predicate (a probe outside that predicate can't
/// use it at all), and a columnstore index has no B-tree to seek in the traditional sense.
/// <paramref name="IsDisabled"/> covers <c>ALTER INDEX ... DISABLE</c> - a disabled index still
/// exists in the catalog (so a later <c>REBUILD</c> can re-enable it) but is genuinely unusable
/// by the engine in the meantime; reporting Indexed=true for it would be the wrong direction for
/// CLAUDE.md's precision discipline. <see cref="CatalogTable.IsIndexedColumn"/> excludes all
/// three.
///
/// <paramref name="IsClustered"/> (docs/detection-checklist.md "DBA-script family sweep" §A,
/// "Heap ... / Clustering-key quality" group) is <c>sys.indexes.type_desc</c> starting with
/// <c>"CLUSTERED"</c> - true for both a traditional rowstore clustered index
/// (<c>type_desc = 'CLUSTERED'</c>) and a clustered columnstore index
/// (<c>'CLUSTERED COLUMNSTORE'</c>), since either one means the table is NOT a heap; false for
/// <c>NONCLUSTERED</c>, <c>NONCLUSTERED COLUMNSTORE</c>, <c>XML</c>, <c>SPATIAL</c>, and
/// <c>NONCLUSTERED HASH</c> (memory-optimized). A clustered columnstore index also sets
/// <see cref="IsColumnstore"/>, so a genuine rowstore clustering KEY (the thing the "clustering-key
/// quality" findings reason about - uniqueness, width, GUID/NEWID default) is always
/// <c>IsClustered &amp;&amp; !IsColumnstore</c>, never <see cref="IsClustered"/> alone. Read
/// LIVE-ONLY (<c>LiveCatalogReader</c>) - matches this codebase's "everything goes via the
/// database" rule: a file-mode <c>CREATE TABLE ... PRIMARY KEY CLUSTERED(...)</c> clause IS
/// parseable, but whether the engine actually built the index that way (a later
/// <c>CREATE CLUSTERED INDEX</c>/<c>ALTER TABLE</c> can change it, and file mode never replays
/// statement history) is exactly the kind of DDL-fidelity reinvention CLAUDE.md rules out. File
/// mode (<c>CatalogBuilder</c>) never sets this field - it defaults to <see langword="false"/> for
/// every index it produces, which would misread a file-mode primary key as "heap with a
/// nonclustered primary key" the instant a consumer looked at it. <c>Predicates.IndexDesignFinding</c>'s
/// own scanner (<c>Predicates.IndexDesignScanner</c>) is therefore only ever invoked from
/// <c>SilentScan.Live.LiveScanRunner</c> after a real live catalog read, never from
/// <c>ScanReportBuilder</c>'s file-mode path - the same live-only-merge pattern
/// <c>TempTableExecShapeFindings</c>/<c>DatabaseConfigurationFindings</c> already established.
///
/// <paramref name="IsHypothetical"/> (docs/detection-checklist.md "DBA-script family sweep" §A,
/// "Disabled and hypothetical indexes") is <c>sys.indexes.is_hypothetical</c> directly - the
/// engine's own precise flag for a Database Engine Tuning Advisor/missing-index-wizard artifact,
/// used instead of a <c>_dta_</c>-name-prefix heuristic once confirmed to exist and be the more
/// reliable signal (a hypothetical index can be named anything at all; the wizard's own default
/// naming convention is a convention, not a guarantee). Microsoft's own documentation states a
/// hypothetical index always carries <c>is_disabled = 1</c> too (it has no real data behind it),
/// so <c>Predicates.IndexDesignScanner</c> checks <see cref="IsHypothetical"/> first and only
/// falls through to a plain disabled-index finding when it is false - never double-reporting the
/// same row under both kinds. Read live-only, same as <see cref="IsClustered"/>; defaults to
/// <see langword="false"/> so file mode (which never sets it) never misreads an ordinary index.
/// </summary>
public sealed record CatalogIndex(
    string? Name,
    CatalogIndexKind Kind,
    bool IsUnique,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<string> IncludedColumns,
    bool IsFiltered = false,
    bool IsColumnstore = false,
    bool IsDisabled = false,
    bool IsClustered = false,
    bool IsHypothetical = false);
