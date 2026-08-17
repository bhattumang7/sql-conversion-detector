namespace SilentScan.Core.Predicates;

public enum IndexDesignFindingKind
{
    /// <summary>A table with no clustered index anywhere (no <c>sys.indexes</c> row with
    /// <c>type = 1</c> CLUSTERED or <c>type = 5</c> CLUSTERED COLUMNSTORE) that nonetheless has
    /// one or more genuine NONCLUSTERED indexes. Every nonclustered index on a heap points back to
    /// its base row with an 8-byte RID (row identifier) instead of the clustering key, and that
    /// RID can change under certain heap maintenance operations (a forwarded-row pointer after a
    /// variable-length column grows past the row's original slot) - a real, documented cost and
    /// fragility distinct from "no clustered index" alone. Deliberately narrower than "this table
    /// is a heap": a heap with ZERO indexes at all (a staging/bulk-load table, often a deliberate
    /// design) is excluded on purpose - see this kind's own SARIF rule text.</summary>
    HeapWithNonclusteredIndexes,

    /// <summary>The sharper sibling of <see cref="HeapWithNonclusteredIndexes"/>: the table's own
    /// PRIMARY KEY constraint is itself declared NONCLUSTERED, so the table has no clustered index
    /// anywhere - not an incidental gap, a specific, well-documented anti-pattern (the single most
    /// commonly reached-for uniqueness guarantee on the table is the one guaranteed to cost an RID
    /// lookup on every nonclustered-index seek).</summary>
    HeapWithNonclusteredPrimaryKey,

    /// <summary>A CLUSTERED index (rowstore, not columnstore) that is not unique
    /// (<c>sys.indexes.is_unique = 0</c>). Every nonclustered index on the table silently carries
    /// a copy of the clustering key in its own leaf rows as the row locator, and since this key
    /// permits duplicates, the engine adds a hidden 4-byte "uniquifier" to every duplicate-keyed
    /// row to keep row locators unique internally - extra storage and extra key width, invisible
    /// in the table's own declared schema, paid on every row and multiplied across every other
    /// index on the table.</summary>
    NonUniqueClusteredIndex,

    /// <summary>A CLUSTERED index (rowstore) whose key is wide: more than
    /// <see cref="Predicates.IndexDesignScanner.WideClusteredKeyMaxColumns"/> key columns, or more
    /// than <see cref="Predicates.IndexDesignScanner.WideClusteredKeyMaxBytes"/> total estimated
    /// key bytes (computed from the already-modeled column types/lengths this catalog already
    /// reads - see <see cref="Predicates.IndexDesignScanner.EstimateColumnKeyBytes"/>). Every
    /// nonclustered index on the table carries a full copy of this key in every leaf row, so a
    /// wide clustering key multiplies its own storage/IO cost across every other index on the
    /// table, not just itself. Thresholds calibrated against the real distribution of clustered
    /// indexes in this project's own local production-shaped test database before being kept
    /// (docs/detection-checklist.md carries the measured numbers) - <see
    /// cref="Predicates.FindingConfidence.Medium"/>, not High, since a threshold-based judgment
    /// call is inherently softer than a structurally-provable fact.</summary>
    WideClusteredKey,

    /// <summary>A CLUSTERED index (rowstore) whose leading (or sole) key column is
    /// <c>uniqueidentifier</c>-typed and carries a column DEFAULT of <c>NEWID()</c>. NEWID()
    /// generates values in genuinely random order, so every insert lands at a random point in the
    /// clustered B-tree instead of at the end - severe page splits and fragmentation, one of the
    /// most well-documented SQL Server anti-patterns there is. <c>NEWSEQUENTIALID()</c> is the
    /// precision-guarding near-miss that must NOT fire here: it generates values that increase
    /// sequentially (not fully server-restart-ordered, but monotonic within a boot cycle), which
    /// avoids the random-insert problem this kind targets - matched by exact default-text equality
    /// after stripping whitespace/parentheses, never a substring match (which would otherwise also
    /// match inside "NEWSEQUENTIALID()" - verified it does not: "NEWID(" is not a substring of
    /// "NEWSEQUENTIALID()").</summary>
    RandomClusteredKeyGuidDefault,
}

/// <summary>
/// docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §A "Physical/schema
/// design", the clustered/nonclustered-flag-dependent group: "Heap (no clustered index) on a
/// table that has nonclustered indexes"/"heap with a nonclustered primary key", plus the three
/// "Clustering-key quality" findings (non-unique clustered index, wide clustered key,
/// <c>uniqueidentifier</c> clustered key with a <c>NEWID()</c> default). The checklist's own prose
/// calls this "four items in this group" needing the new <see cref="Catalog.CatalogIndex.IsClustered"/>
/// flag - the actual count shipped here is FIVE distinct <see cref="Kind"/> members (2 heap +
/// 3 clustering-key-quality), not four; the checklist entry is corrected in place to note this
/// reconciliation rather than silently shipping a different count than what was scoped.
///
/// One finding type, one <see cref="Kind"/> discriminator - this codebase's established
/// shared-plumbing shape (<see cref="ControlFlowRiskFinding"/>/<see cref="SecurityFinding"/>).
/// Catalog-only, no AST walk of any kind - every one of these five is a structural fact about
/// <see cref="Catalog.DatabaseCatalog.Tables"/> alone, computed once by
/// <see cref="IndexDesignScanner"/>. Live-mode only by construction: <see
/// cref="Catalog.CatalogIndex.IsClustered"/> is populated only by <c>LiveCatalogReader</c> (see its
/// own doc comment for why file-mode DDL-fidelity replication is deliberately out of scope), so
/// this stream is always empty from <see cref="Reporting.ScanReportBuilder"/> and is merged in by
/// <c>SilentScan.Live.LiveScanRunner</c> after a real live catalog read - the same pattern
/// <c>TempTableExecShapeFindings</c>/<c>DatabaseConfigurationFindings</c> already established.
///
/// Every table is checked against <see cref="Catalog.CatalogTable.IsMemoryOptimized"/> first and
/// skipped entirely if true, for both heap kinds: a memory-optimized table has no on-disk heap/RID
/// storage at all and is structurally guaranteed to have at least one HASH or NONCLUSTERED
/// (BW-tree) index with no <c>type = 1</c> row ever - reporting "heap" there would be a
/// false-positive rooted in a completely different storage engine, not a real design smell.
///
/// Confidence: <see cref="FindingConfidence.High"/> for <see
/// cref="IndexDesignFindingKind.HeapWithNonclusteredIndexes"/>, <see
/// cref="IndexDesignFindingKind.HeapWithNonclusteredPrimaryKey"/>, and <see
/// cref="IndexDesignFindingKind.NonUniqueClusteredIndex"/> - each a structurally-provable catalog
/// fact with no threshold or estimation involved. <see cref="FindingConfidence.Medium"/> for <see
/// cref="IndexDesignFindingKind.WideClusteredKey"/> - a calibrated but still threshold-based
/// judgment call. <see cref="FindingConfidence.High"/> again for <see
/// cref="IndexDesignFindingKind.RandomClusteredKeyGuidDefault"/> - the DEFAULT text match is exact,
/// not a heuristic, and the fragmentation consequence is long-established, uncontroversial SQL
/// Server storage-engine behavior (Microsoft's own documentation on GUID primary keys), stated
/// directly without a fresh oracle probe - the same "catalog-only structural fact needs no oracle"
/// precedent <see cref="MaxTypedColumnFinding"/> already set, since a static verdict never depends
/// on the cardinality estimator (CLAUDE.md) and this claim is about physical insert order, not a
/// plan shape. Still confirmed once, empirically, against the standing disposable Docker instance
/// (docs/detection-checklist.md carries the measured fragmentation numbers) as an extra,
/// on-brand check - not because the claim needed it to ship.
///
/// Engine-version sensitivity: none of these five depends on compat level or CE mode - clustered
/// index mechanics (the hidden uniquifier, RID-based nonclustered lookups on a heap,
/// GUID-vs-sequential insert locality) are long-standing physical storage-engine behavior, not
/// query-optimizer behavior.
/// </summary>
public sealed record IndexDesignFinding(
    IndexDesignFindingKind Kind,
    string TableQualifiedName,
    string? IndexName,
    string DetailText,
    string SourcePath,
    int Line,
    FindingConfidence Confidence = FindingConfidence.High);
