using System.Text.Json.Serialization;
using SilentScan.Core.TypeInference;

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

    /// <summary>
    /// Two active (non-disabled) indexes on the same table whose ordered key-column lists are
    /// exactly identical, with <see cref="Catalog.CatalogIndex.IsUnique"/> and
    /// <see cref="Catalog.CatalogIndex.Kind"/> also identical between them - a real, deterministic
    /// duplicate object, not merely a similar one. Precision guard, straight from the checklist:
    /// a filtered index is never compared this way at all (this pass reads only
    /// <see cref="Catalog.CatalogIndex.IsFiltered"/>, not the filter predicate's own text, so two
    /// filtered indexes' definitions can never be confirmed equal here - excluded rather than
    /// guessed about), and neither is a columnstore index (no ordered B-tree key the same way).
    /// One nonclustered index is pure write amplification and wasted space with zero query benefit
    /// over the other.
    /// </summary>
    DuplicateIndex,

    /// <summary>
    /// One active, unfiltered, non-columnstore index's key-column list is a proper (strictly
    /// shorter) ordered prefix of a second such index's own key-column list on the same table,
    /// with the shorter index's own INCLUDE columns already a subset of the longer index's
    /// INCLUDE columns, and <see cref="Catalog.CatalogIndex.IsUnique"/>/<see cref="Catalog.CatalogIndex.Kind"/>
    /// identical between the two - the same precision guard as <see cref="DuplicateIndex"/>. Any
    /// seek the shorter (subsumed) index could serve, the longer index can also serve (it carries
    /// every one of the shorter index's key columns as its own leading prefix, in the same order,
    /// plus everything the shorter index put in INCLUDE) - so the shorter one is redundant, not
    /// merely similar.
    /// </summary>
    SubsumedIndex,

    /// <summary>
    /// A real FOREIGN KEY constraint's own full parent-side column set has no active, non-filtered,
    /// non-columnstore index leading on it (the index's own first N key columns, N = the FK's own
    /// column count, form exactly this column set - a composite-aware, order-tolerant-on-the-FK-side
    /// comparison, the same shape <see cref="NonUniqueUpdateSourceScanner"/>'s own uniqueness check
    /// and <see cref="PartialCompositeForeignKeyJoinScanner"/> already use elsewhere in this
    /// codebase). Every parent-side DELETE/UPDATE the engine must referential-integrity-check
    /// against the child table, and every join application code writes along this relationship,
    /// has no seek path and forces a full scan of the child table instead.
    /// </summary>
    UnindexedForeignKey,

    /// <summary>
    /// <c>ALTER INDEX ... DISABLE</c> left in place (<see cref="Catalog.CatalogIndex.IsDisabled"/>,
    /// already read and already relied on elsewhere in this codebase to exclude a disabled index
    /// from seek eligibility - reported here as a finding of its own for the first time). A
    /// disabled index still occupies catalog metadata and blocks a same-named
    /// <c>CREATE INDEX</c>, but serves no query the engine runs today. Never fires for a
    /// <see cref="HypotheticalIndex"/> row - see that kind's own doc comment for why.
    /// </summary>
    DisabledIndex,

    /// <summary>
    /// A Database Engine Tuning Advisor/missing-index-wizard leftover
    /// (<see cref="Catalog.CatalogIndex.IsHypothetical"/>, <c>sys.indexes.is_hypothetical</c> read
    /// directly - see that field's own doc comment for why the precise engine flag is used instead
    /// of a <c>_dta_</c>-name-prefix heuristic). A hypothetical index has no real data behind it at
    /// all; it exists purely so the tuning advisor's own what-if analysis could reason about it,
    /// and is meant to be dropped once that analysis session ends - one left behind in a live
    /// schema is pure clutter with zero query benefit, not a design choice anyone made on purpose.
    /// </summary>
    HypotheticalIndex,

    /// <summary>
    /// A table carries at least <see cref="Predicates.IndexDesignScanner.ManyNonclusteredIndexesThreshold"/>
    /// active, non-clustered indexes. Threshold-based, deliberately lower precision than the rest
    /// of this group's structurally-provable kinds - the checklist's own explicit instruction:
    /// report only that the table "carries N indexes, each paid for on every write", never "drop
    /// this one" - that second claim needs production usage statistics (which index is actually
    /// read, by which query) this catalog-only pass structurally cannot see.
    /// </summary>
    ManyNonclusteredIndexes,

    /// <summary>
    /// A single active, non-clustered, non-columnstore index carries at least
    /// <see cref="Predicates.IndexDesignScanner.ManyKeyColumnsThreshold"/> key columns. Distinct
    /// from <see cref="WideClusteredKey"/> (which is scoped to the CLUSTERED key specifically, at
    /// its own, tighter 3-column/16-byte thresholds, and is never re-reported here even if it also
    /// clears this threshold - excluded by construction, not overlap left unhandled).
    /// </summary>
    ManyKeyColumnsIndex,

    /// <summary>
    /// A table's own column count is at least <see cref="Predicates.IndexDesignScanner.WideTableMinColumns"/>,
    /// or its estimated total non-LOB row width (the same best-effort per-column byte estimate
    /// <see cref="Predicates.IndexDesignScanner.EstimateColumnKeyBytes"/> already computes for a
    /// clustering key, summed across every column whose type resolves - a LOB/MAX/unresolved
    /// column contributes nothing to this sum rather than guessed-at bytes, so the reported total
    /// is always a safe lower bound, never an overstatement) exceeds
    /// <see cref="Predicates.IndexDesignScanner.WideTableMaxNonLobBytes"/> bytes. Genuinely the
    /// lowest-precision kind in this whole stream - listed in the checklist "for completeness
    /// rather than as a priority": a wide table is a data-modeling signal (worth a second look at
    /// normalization or hot/cold column separation), not a specific, provable defect this pass can
    /// point at. <see cref="FindingConfidence.Low"/> always.
    /// </summary>
    WideTable,

    /// <summary>
    /// A table with at least <see cref="Predicates.IndexDesignScanner.RatioChecksMinColumns"/>
    /// columns has a nullable-column fraction at or above
    /// <see cref="Predicates.IndexDesignScanner.HighNullableColumnRatioThreshold"/>. The same
    /// "listed for completeness" framing as <see cref="WideTable"/>: often correlates with an
    /// overloaded table (several optional sub-entities crammed into one row) but this pass cannot
    /// confirm that for any specific column. <see cref="FindingConfidence.Low"/> always.
    /// </summary>
    HighNullableColumnRatio,

    /// <summary>
    /// A table with at least <see cref="Predicates.IndexDesignScanner.RatioChecksMinColumns"/>
    /// columns has a string-family-column fraction (<see cref="TypeInference.SqlType.IsStringFamily"/>)
    /// at or above <see cref="Predicates.IndexDesignScanner.HighStringColumnRatioThreshold"/>. The
    /// same "listed for completeness" framing as <see cref="WideTable"/>: often correlates with
    /// under-typed data (dates/numbers/enums stored as text with no CHECK/FK narrowing) but this
    /// pass cannot confirm that for any specific column. <see cref="FindingConfidence.Low"/> always.
    /// </summary>
    HighStringColumnRatio,

    /// <summary>
    /// A filtered index (<see cref="Catalog.CatalogIndex.IsFiltered"/>) whose own filter predicate
    /// (<see cref="Catalog.CatalogIndex.FilterDefinition"/>, reparsed through the same throwaway-
    /// wrapper-statement technique <see cref="SchemaDependencyScanner"/> uses for a CHECK
    /// constraint's own definition text) references at least one column that is NOT among this
    /// index's own <see cref="Catalog.CatalogIndex.KeyColumns"/>/<see cref="Catalog.CatalogIndex.IncludedColumns"/>.
    /// The engine can only substitute a filtered index for a query whose own WHERE clause restates
    /// (or logically implies) the filter predicate - when the filter references a column the index
    /// itself does not carry, the optimizer would additionally need to re-derive that column's
    /// value from the base table to even confirm the filter still holds, which defeats the covering
    /// benefit a filtered index exists for in the first place. Only fires when the filter text
    /// reparses cleanly - a filter this pass cannot parse is left unanalyzed rather than guessed
    /// at, the same "never guess" discipline <see cref="DuplicateIndex"/>/<see cref="SubsumedIndex"/>
    /// already apply to an unfiltered index's own definition.
    /// </summary>
    FilterColumnNotInIndex,

    /// <summary>
    /// A column declared <c>text</c>, <c>ntext</c>, or <c>image</c> - all three formally deprecated
    /// by Microsoft since SQL Server 2005 in favor of <c>varchar(max)</c>/<c>nvarchar(max)</c>/
    /// <c>varbinary(max)</c>, and Microsoft's own documentation states outright that a future
    /// version may remove them entirely. A genuine functional deprecation, not merely a naming
    /// recommendation: these three types cannot be used in most string functions, cannot appear in
    /// a WHERE/GROUP BY/ORDER BY without extra casting gymnastics, and cannot be a variable/
    /// parameter type in many contexts the MAX-length equivalents support natively. Catalog-only -
    /// a structural fact about the column's declared type, independent of whether any scanned query
    /// touches it (the same shape <see cref="MaxTypedColumnFinding"/> already established).
    /// </summary>
    DeprecatedLobColumnType,

    /// <summary>
    /// A column declared <c>timestamp</c> - deliberately NOT grouped with <see cref="DeprecatedLobColumnType"/>:
    /// unlike <c>text</c>/<c>ntext</c>/<c>image</c>, <c>timestamp</c> is not a distinct, functionally
    /// deprecated type at all. Since SQL Server 2005, <c>rowversion</c> is literally a synonym for
    /// the exact same underlying 8-byte auto-incrementing binary type - <c>sys.columns</c>/<c>sys.types</c>
    /// report a <c>rowversion</c>-declared column identically to a <c>timestamp</c>-declared one (both
    /// resolve to system type id 80, name "timestamp"; there is no separate "rowversion" row in
    /// <c>sys.types</c> to tell them apart at the catalog level - confirmed directly against the
    /// engine, not assumed). Microsoft's own documentation recommends <c>rowversion</c> for new
    /// development purely because the name no longer collides with the unrelated SQL-standard
    /// `TIMESTAMP` datetime type and reads correctly - a naming-only recommendation, not a functional
    /// deprecation, and worded/confidence-scored accordingly (<see cref="FindingConfidence.Low"/>,
    /// informational).
    /// </summary>
    TimestampColumnNaming,

    /// <summary>
    /// A <c>float</c> or <c>real</c> (approximate, IEEE-754 binary floating-point) column used as an
    /// index key column - structurally risky regardless of any specific query, since an approximate
    /// type cannot represent every decimal value exactly, and a value computed two logically-
    /// equivalent-but-differently-rounded ways can compare unequal under <c>=</c> even though a
    /// person would call them "the same number". An index built on such a column still works as a
    /// B-tree (the bytes it stores are exact even though the values they represent are not), but any
    /// equality seek/comparison against it inherits the same representation-error correctness risk
    /// the sibling AST-level finding (a `float`/`real` column compared with `=`) targets more
    /// specifically - this catalog-only half flags the structural shape (the column is a key at
    /// all), the AST-level half flags an actual equality predicate against one. Catalog-only,
    /// independent of whether any scanned query happens to compare on it.
    /// </summary>
    FloatOrRealIndexKeyColumn,

    /// <summary>
    /// A statistics object (<see cref="Catalog.CatalogStatisticsInfo"/>) explicitly created/altered
    /// <c>WITH NORECOMPUTE</c> - docs/detection-checklist.md "DBA-script family sweep" §A
    /// "Statistics-object flags", the <c>NO_RECOMPUTE</c> half; the sibling half of that same
    /// checklist item ("a partitioned table with no incremental statistics") is deliberately NOT
    /// shipped alongside this one - this codebase's catalog surface reads no partition metadata at
    /// all (the same gap, and the same reasoning, already recorded against the separate
    /// "Non-aligned index on a partitioned table" item elsewhere in this file: zero partitioned
    /// tables exist in the local test database to validate new plumbing against, confirmed directly
    /// via <c>sys.partition_schemes</c> before deciding). A stats object marked <c>NORECOMPUTE</c>
    /// never gets refreshed by the engine's own automatic statistics-update maintenance - it drifts
    /// silently stale as the table's data changes, which is the actual mechanism (not "statistics
    /// ARE stale right now", a live data-state fact this pass structurally cannot see) this catalog
    /// flag can honestly claim.
    /// </summary>
    NoRecomputeStatistics,

    /// <summary>
    /// docs/detection-checklist.md full-archive practitioner sweep §E, "Column too wide to ever be
    /// an index key" - CORRECTED in scope after direct oracle verification (2026-08-18), not built
    /// as originally worded. The checklist item claimed <c>CREATE INDEX</c> "hard-fails" once a
    /// column's declared max byte width exceeds the engine's key-length ceiling; verified directly
    /// against the standing Docker oracle that this is true ONLY for a FIXED-length type (<c>char</c>/
    /// <c>nchar</c>/<c>binary</c>) - which the engine already refuses to compile at all
    /// (<c>CREATE INDEX</c> itself fails, Msg 1944/1946-family), the exact "hard DDL-time engine
    /// error, not a silent defect" shape this checklist's own second-sweep §H already excludes
    /// elsewhere (flagging something the engine already refuses to compile adds no value beyond its
    /// own error message). For a VARIABLE-length type (<c>varchar</c>/<c>nvarchar</c>/<c>varbinary</c>,
    /// non-MAX) confirmed the opposite: <c>CREATE INDEX</c> SUCCEEDS with only a printed warning
    /// (easily swallowed by deployment tooling that doesn't surface SQL warnings) - the real failure
    /// is deferred to a future <c>INSERT</c>/<c>UPDATE</c> that finally stores a long-enough value
    /// (Msg 1946 "Operation failed... exceeds the maximum length"), silently, possibly years later
    /// in production. That deferred-failure shape genuinely IS this codebase's target pattern, so
    /// this kind ships scoped to variable-length key columns only. Also corrected the ceiling
    /// itself: NOT a flat 900 bytes for every index type as the checklist assumed - confirmed
    /// 900 bytes for a CLUSTERED index/PRIMARY KEY/UNIQUE constraint's own key
    /// (<see cref="Catalog.CatalogIndex.IsClustered"/>), 1700 bytes for a NONCLUSTERED index's own
    /// key, both exact engine-stated ceilings reproduced verbatim from the oracle's own warning
    /// text. Catalog-only: reuses <see cref="Predicates.IndexDesignScanner.EstimateColumnKeyBytes"/>
    /// against each active (non-disabled) index's own key columns, restricted to
    /// <see cref="TypeInference.SqlTypeCategory.VarChar"/>/<see cref="TypeInference.SqlTypeCategory.NVarChar"/>/
    /// <see cref="TypeInference.SqlTypeCategory.VarBinary"/> (non-MAX, since MAX already estimates
    /// <see langword="null"/> and SQL Server refuses a MAX column as a key column outright - a
    /// separate, already-engine-blocked case, not this one).
    /// </summary>
    VariableLengthKeyColumnExceedsKeyLimit,

    /// <summary>
    /// docs/detection-checklist.md second full-archive practitioner sweep §G, "Indexes sharing an
    /// identical key-column list and sort direction but with different, non-overlapping INCLUDE
    /// sets". Distinct from <see cref="DuplicateIndex"/> (identical key list AND identical INCLUDE
    /// list) and <see cref="SubsumedIndex"/> (a proper key-list PREFIX relationship) - the
    /// divergence here is ONLY in the INCLUDE columns: same key columns, same order, same per-column
    /// sort direction (<see cref="Catalog.CatalogIndex.KeyColumnIsDescending"/>), same
    /// <see cref="Catalog.CatalogIndex.IsUnique"/>/<see cref="Catalog.CatalogIndex.Kind"/> - the
    /// same precision guards <see cref="DuplicateIndex"/> already applies - but each index's own
    /// <see cref="Catalog.CatalogIndex.IncludedColumns"/> set is genuinely non-overlapping with the
    /// other's (neither is a subset of the other - a subset relationship there would already make
    /// one of them <see cref="SubsumedIndex"/> instead, so this never double-reports the same pair
    /// under both kinds). Each index individually looks legitimate (built for a different query),
    /// but they are mergeable into one index carrying the union of both INCLUDE lists, at no seek
    /// cost to either original query, for less write/storage overhead than carrying both separately.
    /// Only ever compared when BOTH indexes' own <see cref="Catalog.CatalogIndex.KeyColumnIsDescending"/>
    /// is non-empty (i.e. actually read from a live catalog) - an empty ("unknown") sort-direction
    /// list on either side means this pass cannot confirm the sort direction genuinely matches, and
    /// never guesses that it does.
    /// </summary>
    MergeableIndexesDifferingIncludeOnly,

    /// <summary>
    /// docs/detection-checklist.md full-archive practitioner sweep §E, "Columnstore index present on
    /// a table that is also a live DML target of transactional code" - shipped as a STRUCTURAL RISK
    /// FLAG ONLY, exactly as the checklist's own instruction requires, never a proven-cost claim.
    /// Mechanism confirmed directly against the standing Docker oracle (2026-08-18): a single-row
    /// <c>DELETE</c> inside an explicit transaction against a table carrying a clustered columnstore
    /// index takes a real <c>ROWGROUP</c>-granularity lock (<c>sys.dm_tran_locks</c>,
    /// <c>resource_type = 'ROWGROUP'</c>, mode <c>UIX</c>) - not a per-row lock the way an ordinary
    /// rowstore DELETE takes, so unrelated concurrent access to every OTHER row sharing that same
    /// rowgroup can genuinely block behind this one row's transaction. Catalog-decidable: the table
    /// carries a columnstore index (<see cref="Catalog.CatalogIndex.IsColumnstore"/>, any of
    /// clustered or nonclustered columnstore) AND is a direct INSERT/UPDATE/DELETE/MERGE target
    /// somewhere in the scanned corpus (the same direct-target-only scope
    /// <see cref="CrossModuleLockOrderScanner"/>'s own write-target visitor already uses - never
    /// through a view, never through dynamic SQL this pass can't see inside). Whether contention
    /// actually occurs is workload-dependent (concurrent access pattern, rowgroup size, whether the
    /// DML actually lands in a compressed rowgroup vs. the deltastore) and structurally out of
    /// reach for a static pass - stated as an explicit scope limit in the finding text itself, the
    /// same discipline <see cref="MonotonicClusteredKeyMissingSequentialOptimization"/> uses.
    /// </summary>
    ColumnstoreIndexOnDmlTargetTable,

    /// <summary>
    /// docs/detection-checklist.md second full-archive practitioner sweep §G, "Monotonically
    /// increasing clustered key ... with no OPTIMIZE_FOR_SEQUENTIAL_KEY" - the precise mirror image
    /// of <see cref="RandomClusteredKeyGuidDefault"/>: one direction (random) fragments the whole
    /// clustered B-tree, the other (monotonic) hotspots a single trailing page instead, since every
    /// insert lands immediately after the last row - concurrent inserts can serialize on that one
    /// page's latch. Shipped as a STRUCTURAL RISK FLAG ONLY, same discipline as
    /// <see cref="ColumnstoreIndexOnDmlTargetTable"/>: the structural precondition is catalog-
    /// decidable, but whether it actually causes contention depends on concurrent insert rate, which
    /// is workload data this pass cannot see - stated as an explicit scope limit in the finding text
    /// itself. <c>OPTIMIZE_FOR_SEQUENTIAL_KEY</c> confirmed directly against the standing Docker
    /// oracle (SQL Server 2022, but the option shipped originally in SQL Server 2019 CU5) as a real,
    /// current index-level option, and <c>sys.indexes.optimize_for_sequential_key</c> confirmed to
    /// read back the real per-index on/off state (0 by default, flips to 1 immediately after
    /// <c>ALTER INDEX ... SET (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON)</c>) - so this kind never
    /// false-positives against an index that already carries the mitigation
    /// (<see cref="Catalog.CatalogIndex.OptimizeForSequentialKey"/>). Scoped to the clear,
    /// high-confidence case only, per the checklist's own instruction: the clustered index's leading
    /// key column is an <c>IDENTITY</c> column (<see cref="Catalog.CatalogColumn.IsIdentity"/>) with
    /// a positive <see cref="Catalog.CatalogColumn.IdentityIncrement"/> (a negative or zero increment
    /// is not "always-ascending" and is deliberately excluded rather than guessed about) - broadening
    /// to other monotonic-by-construction patterns (a sequence-defaulted column, an ever-increasing
    /// datetime default) was evaluated and NOT done: this pass has no cheap, precise way to prove a
    /// non-IDENTITY column is monotonic from the catalog alone without risking a false positive.
    /// </summary>
    MonotonicClusteredKeyMissingSequentialOptimization,

    /// <summary>
    /// docs/detection-checklist.md "Non-aligned index on a partitioned table". The base table is
    /// genuinely partitioned - its own clustered (non-columnstore) index carries a real
    /// <see cref="Catalog.CatalogIndex.PartitionSchemeName"/> - but another active index on the
    /// same table is NOT aligned with it: either that index sits on a plain, unpartitioned
    /// filegroup while the table itself is partitioned, or it shares the identical partition
    /// scheme object but is keyed on a different column than the table's own partitioning column.
    /// Both shapes confirmed as real, catalog-visible, distinct <c>sys.data_spaces</c> facts
    /// directly against the standing Docker instance (2026-08-20; see
    /// <see cref="Catalog.CatalogIndex.PartitionSchemeName"/>'s own doc comment for the exact
    /// probe). This is documented, standard SQL Server terminology ("aligned"/"non-aligned"
    /// index) - Microsoft's own documentation states a non-aligned index cannot participate in a
    /// partition SWITCH against the table at all, and per-partition maintenance (rebuild/
    /// reorganize one partition) degrades to a full-index operation for a non-aligned index
    /// specifically, since the engine has no per-partition boundary to act on for it; only the
    /// catalog shape (not the SWITCH failure itself) was independently confirmed here. Scoped to
    /// the table's own CLUSTERED, non-columnstore index as the alignment reference only - a
    /// partitioned heap (no clustered index at all) is out of scope, the same "never guess without
    /// an anchor" limit this scanner's clustering-key-quality checks already apply elsewhere; a
    /// columnstore candidate index is excluded from the comparison for the same reason columnstore
    /// is excluded from every other structural key-shape check in this file.
    /// </summary>
    NonAlignedPartitionedIndex,
}

/// <summary>
/// docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §A "Physical/schema design".
/// Two groups now share this one type/scanner:
///
/// <b>The original clustered/nonclustered-flag-dependent group</b> (five kinds): "Heap (no
/// clustered index) on a table that has nonclustered indexes"/"heap with a nonclustered primary
/// key", plus the three "Clustering-key quality" findings (non-unique clustered index, wide
/// clustered key, <c>uniqueidentifier</c> clustered key with a <c>NEWID()</c> default). The
/// checklist's own prose called this "four items in this group" needing the new
/// <see cref="Catalog.CatalogIndex.IsClustered"/> flag - the actual count shipped was FIVE distinct
/// <see cref="Kind"/> members (2 heap + 3 clustering-key-quality), not four; noted here rather than
/// silently shipping a different count than what was scoped.
///
/// <b>The remaining catalog-only §A items</b> (nine more kinds, same "one Kind enum, one shared
/// finding shape" convention rather than fragmenting into another type for closely-related
/// catalog-only index-shape facts): duplicate/subsumed indexes, unindexed foreign keys, disabled/
/// hypothetical indexes, over-indexing (many nonclustered indexes on one table, and any single
/// index with too many key columns), and the three lowest-precision "listed for completeness"
/// table-shape signals (wide table, high nullable-column ratio, high string-column ratio). See
/// each <see cref="Kind"/> member's own doc comment for its precision guard. "Table with no primary
/// key at all" and "non-aligned index on a partitioned table" are the checklist's other two items
/// in this group - the former is an exact duplicate of the already-shipped
/// <c>Predicates.StatementShapeFindingKind.TableWithNoPrimaryKey</c> (cross-referenced, not
/// rebuilt); the latter was evaluated and deliberately NOT shipped - see the checklist entry
/// itself for why (new catalog plumbing with zero real rows in the local test database to validate
/// against).
///
/// <b>A third §A wave</b> (four more kinds, same reasoning as the second wave for staying on this
/// one type): <see cref="IndexDesignFindingKind.FilterColumnNotInIndex"/> (needs <see
/// cref="Catalog.CatalogIndex.FilterDefinition"/>, reparsed the same throwaway-wrapper-statement
/// way <see cref="SchemaDependencyScanner"/> already reparses a CHECK constraint's own definition
/// text), <see cref="IndexDesignFindingKind.DeprecatedLobColumnType"/>/<see
/// cref="IndexDesignFindingKind.TimestampColumnNaming"/> (a plain column-type walk, the checklist's
/// "deprecated LOB column types" item, split into a genuine functional deprecation and a
/// naming-only recommendation - see each kind's own doc comment for why they are NOT the same
/// claim), and <see cref="IndexDesignFindingKind.FloatOrRealIndexKeyColumn"/> (the catalog-only
/// half of the checklist's "float/real as an index key or equality-predicate target" item - the
/// AST-level half, an actual equality predicate against a float/real column, ships as its own
/// small type, <see cref="FloatEqualityFinding"/>, since it is a predicate-site claim, not a
/// catalog-only structural one - see that type's own doc comment for why it was not folded into
/// <see cref="TypedPredicateExtractor"/>'s existing type-conversion-verdict machinery instead).
/// The checklist's identity/sequence-range item ships as its own type too, <see
/// cref="IdentityRangeFinding"/> - its own doc comment explains why "one is a schema fact, the
/// other needs live data state" earns it a stand-alone type rather than two more members here.
///
/// One finding type, one <see cref="Kind"/> discriminator - this codebase's established
/// shared-plumbing shape (<see cref="ControlFlowRiskFinding"/>/<see cref="SecurityFinding"/>).
/// Catalog-only, no AST walk of any kind except <see cref="IndexDesignFindingKind.FilterColumnNotInIndex"/>'s
/// own throwaway reparse of a filter's stored definition TEXT (not a query-site AST - the
/// distinction <see cref="SchemaDependencyScanner"/> already draws for CHECK constraints) - every
/// other one of these nineteen kinds is a structural fact about
/// about <see cref="Catalog.DatabaseCatalog.Tables"/>/<see cref="Catalog.DatabaseCatalog.ForeignKeys"/>
/// alone, computed once by <see cref="IndexDesignScanner"/>. Live-mode only by construction: <see
/// cref="Catalog.CatalogIndex.IsClustered"/>/<see cref="Catalog.CatalogIndex.IsHypothetical"/>/<see
/// cref="Catalog.CatalogIndex.FilterDefinition"/> are populated only by <c>LiveCatalogReader</c>
/// (see their own doc comments for why file-mode DDL-fidelity replication is deliberately out of
/// scope) - the newer <see cref="IndexDesignFindingKind.DeprecatedLobColumnType"/>/<see
/// cref="IndexDesignFindingKind.TimestampColumnNaming"/>/<see cref="IndexDesignFindingKind.FloatOrRealIndexKeyColumn"/>
/// kinds are structurally derivable from a plain column-type walk that file mode COULD populate,
/// but are kept on this same live-only-invoked scanner rather than fragmenting invocation across
/// two call sites for three kinds alone - so this stream is always empty from
/// <see cref="Reporting.ScanReportBuilder"/> and is merged in by <c>SilentScan.Live.LiveScanRunner</c>
/// after a real live catalog read - the same pattern <c>TempTableExecShapeFindings</c>/
/// <c>DatabaseConfigurationFindings</c> already established.
///
/// Every table is checked against <see cref="Catalog.CatalogTable.IsMemoryOptimized"/> first and
/// skipped entirely if true, for both heap kinds: a memory-optimized table has no on-disk heap/RID
/// storage at all and is structurally guaranteed to have at least one HASH or NONCLUSTERED
/// (BW-tree) index with no <c>type = 1</c> row ever - reporting "heap" there would be a
/// false-positive rooted in a completely different storage engine, not a real design smell.
///
/// Confidence: <see cref="FindingConfidence.High"/> for <see
/// cref="IndexDesignFindingKind.HeapWithNonclusteredIndexes"/>, <see
/// cref="IndexDesignFindingKind.HeapWithNonclusteredPrimaryKey"/>, <see
/// cref="IndexDesignFindingKind.NonUniqueClusteredIndex"/>, <see
/// cref="IndexDesignFindingKind.DuplicateIndex"/>, <see cref="IndexDesignFindingKind.SubsumedIndex"/>,
/// <see cref="IndexDesignFindingKind.UnindexedForeignKey"/>, <see cref="IndexDesignFindingKind.DisabledIndex"/>,
/// and <see cref="IndexDesignFindingKind.HypotheticalIndex"/> - each a structurally-provable
/// catalog fact with no threshold or estimation involved. <see cref="FindingConfidence.Medium"/>
/// for <see cref="IndexDesignFindingKind.WideClusteredKey"/>, <see
/// cref="IndexDesignFindingKind.ManyNonclusteredIndexes"/>, and <see
/// cref="IndexDesignFindingKind.ManyKeyColumnsIndex"/> - each a calibrated but still
/// threshold-based judgment call. <see cref="FindingConfidence.High"/> again for <see
/// cref="IndexDesignFindingKind.RandomClusteredKeyGuidDefault"/> - the DEFAULT text match is exact,
/// not a heuristic, and the fragmentation consequence is long-established, uncontroversial SQL
/// Server storage-engine behavior (Microsoft's own documentation on GUID primary keys), stated
/// directly without a fresh oracle probe - the same "catalog-only structural fact needs no oracle"
/// precedent <see cref="MaxTypedColumnFinding"/> already set, since a static verdict never depends
/// on the cardinality estimator (CLAUDE.md) and this claim is about physical insert order, not a
/// plan shape. Still confirmed once, empirically, against the standing disposable Docker instance
/// (docs/detection-checklist.md carries the measured fragmentation numbers) as an extra,
/// on-brand check - not because the claim needed it to ship. <see cref="FindingConfidence.Low"/>
/// always for <see cref="IndexDesignFindingKind.WideTable"/>, <see
/// cref="IndexDesignFindingKind.HighNullableColumnRatio"/>, and <see
/// cref="IndexDesignFindingKind.HighStringColumnRatio"/> - the checklist's own "lower-precision,
/// listed for completeness rather than as priorities" framing, kept genuinely informational rather
/// than dropped, after real measurement against the local test database showed all three fire on
/// a real minority of tables rather than nearly all of them (docs/detection-checklist.md carries
/// the measured numbers). <see cref="FindingConfidence.High"/> for <see
/// cref="IndexDesignFindingKind.FilterColumnNotInIndex"/> (deterministic once the filter text
/// parses) and <see cref="IndexDesignFindingKind.DeprecatedLobColumnType"/>/<see
/// cref="IndexDesignFindingKind.FloatOrRealIndexKeyColumn"/> (plain declared-type facts). <see
/// cref="FindingConfidence.Low"/> for <see cref="IndexDesignFindingKind.TimestampColumnNaming"/> -
/// a naming-only recommendation, not a defect. <see cref="FindingConfidence.High"/> again for
/// <see cref="IndexDesignFindingKind.NonAlignedPartitionedIndex"/> - the partition-scheme/
/// partitioning-column mismatch is a deterministic <c>sys.data_spaces</c>/<c>sys.index_columns</c>
/// catalog fact, not an estimation.
///
/// Engine-version sensitivity: none of these nineteen kinds depends on compat level or CE mode -
/// clustered index mechanics (the hidden uniquifier, RID-based nonclustered lookups on a heap,
/// GUID-vs-sequential insert locality), duplicate/subsumed/disabled/hypothetical/filtered index
/// catalog state, foreign-key/index catalog shape, table column-shape statistics, and declared
/// column types are all long-standing physical storage-engine/catalog facts, not query-optimizer
/// behavior.
/// </summary>
public sealed record IndexDesignFinding(
    IndexDesignFindingKind Kind,
    string TableQualifiedName,
    string? IndexName,
    string DetailText,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

