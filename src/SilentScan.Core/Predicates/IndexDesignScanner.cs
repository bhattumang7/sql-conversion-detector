using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Catalog-only pass for all fourteen <see cref="IndexDesignFindingKind"/> members - see
/// <see cref="IndexDesignFinding"/>'s own doc comment for the full scope/precision story. Walks
/// <see cref="DatabaseCatalog.Tables"/>/<see cref="DatabaseCatalog.ForeignKeys"/> once, no AST, no
/// query site involved; live-mode only because <see cref="CatalogIndex.IsClustered"/>/
/// <see cref="CatalogIndex.IsHypothetical"/> are live-only (see their own doc comments) - never
/// invoked from file-mode <see cref="Reporting.ScanReportBuilder"/>, only from
/// <c>SilentScan.Live.LiveScanRunner</c> after a real catalog read.
/// </summary>
public static class IndexDesignScanner
{
    /// <summary>Calibrated against the real distribution of clustered indexes in this project's
    /// own local production-shaped test database (docs/detection-checklist.md carries the measured
    /// numbers): of 681 real clustered indexes, only 7 (~1%) exceed 3 key columns - a genuinely
    /// unusual shape worth flagging, not a routine one.</summary>
    public const int WideClusteredKeyMaxColumns = 3;

    /// <summary>Same calibration pass as <see cref="WideClusteredKeyMaxColumns"/>: of 681 real
    /// clustered indexes, the average key width was ~15.3 bytes (many single-column
    /// <c>uniqueidentifier</c> keys sit exactly at 16) and 36 (~5%) exceed 16 bytes - kept at the
    /// checklist's original proposed threshold since the measured distribution shows it firing on
    /// a real, non-trivial minority rather than either the routine case or almost nothing.</summary>
    public const int WideClusteredKeyMaxBytes = 16;

    /// <summary>
    /// Calibrated against the real distribution of active nonclustered indexes per table in this
    /// project's own local production-shaped test database (docs/detection-checklist.md carries
    /// the measured numbers): of 328 tables carrying at least one active nonclustered index, only
    /// 5 (~1.5%) carry 7 or more - a genuinely unusual shape, not the routine case.
    /// </summary>
    public const int ManyNonclusteredIndexesThreshold = 7;

    /// <summary>
    /// The checklist's own stated threshold, kept as proposed after calibration: of 1,227 real
    /// indexes in the local test database, only 1 (~0.08%) carries 7 or more key columns - fires
    /// on a genuine outlier, not a routine shape.
    /// </summary>
    public const int ManyKeyColumnsThreshold = 7;

    /// <summary>The checklist's own stated threshold for a wide table's column count.</summary>
    public const int WideTableMinColumns = 35;

    /// <summary>The checklist's own stated threshold for a wide table's estimated non-LOB row width.</summary>
    public const int WideTableMaxNonLobBytes = 2000;

    /// <summary>
    /// Shared floor for both ratio-based table-shape checks (<see cref="IndexDesignFindingKind.HighNullableColumnRatio"/>/
    /// <see cref="IndexDesignFindingKind.HighStringColumnRatio"/>) - a 2-column mapping table where
    /// both columns happen to be nullable/string-typed trivially hits any ratio threshold without
    /// meaning anything; calibration against the local test database used this same floor.
    /// </summary>
    public const int RatioChecksMinColumns = 5;

    /// <summary>
    /// Calibrated against the local test database (docs/detection-checklist.md carries the measured
    /// numbers): of 835 real tables with at least <see cref="RatioChecksMinColumns"/> columns, 33
    /// (~3.9%) have 80%+ of their columns nullable - a real minority, not the routine case.
    /// </summary>
    public const double HighNullableColumnRatioThreshold = 0.8;

    /// <summary>
    /// Same calibration pass as <see cref="HighNullableColumnRatioThreshold"/>: 9 of 835 tables
    /// (~1.1%) have 80%+ of their columns string-typed.
    /// </summary>
    public const double HighStringColumnRatioThreshold = 0.8;

    /// <summary>
    /// docs/detection-checklist.md full-archive practitioner sweep §E "Columnstore index present on
    /// a table that is also a live DML target of transactional code" - <paramref name="dmlTargetTables"/>
    /// is the set of table qualified names (ordinal-ignore-case) this scan run found a direct
    /// INSERT/UPDATE/DELETE/MERGE target somewhere in the scanned corpus (computed once by the
    /// caller from the same parsed modules the rest of the pipeline already walks, via
    /// <see cref="DmlTargetTableScanner.Scan"/> - never by this catalog-only scanner itself, which
    /// has no AST access of its own). <see langword="null"/> (the default) means the caller never
    /// computed this set at all - e.g. file mode, which never invokes this scanner in the first
    /// place - and <see cref="IndexDesignFindingKind.ColumnstoreIndexOnDmlTargetTable"/> is
    /// correctly never reported rather than treating "no data" as "no DML targets".
    /// </summary>
    public static IReadOnlyList<IndexDesignFinding> Scan(DatabaseCatalog catalog, IReadOnlySet<string>? dmlTargetTables = null)
    {
        var defaultTextByColumn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var expression in catalog.SchemaExpressions)
        {
            if (expression.Kind != SchemaDependencyKind.DefaultConstraint || expression.ColumnName is null)
            {
                continue;
            }

            var key = DefaultKey(expression.TableQualifiedName, expression.ColumnName);
            defaultTextByColumn.TryAdd(key, expression.DefinitionText);
        }

        var findings = new List<IndexDesignFinding>();

        foreach (var table in catalog.Tables)
        {
            // Only a real base table has physical heap/clustered storage at all - a view, temp
            // table, table variable, table type, or CLR TVF's return shape either has no storage
            // of its own or (temp/table-variable) is out of this catalog-only pass's scope.
            if (table.Kind != CatalogTableKind.Table)
            {
                continue;
            }

            ScanHeapFindings(table, findings);
            ScanClusteringKeyQuality(table, defaultTextByColumn, findings);
            ScanDuplicateAndSubsumedIndexes(table, findings);
            ScanDisabledAndHypotheticalIndexes(table, findings);
            ScanOverIndexing(table, findings);
            ScanTableShape(table, findings);
            ScanFilteredIndexColumnCoverage(table, findings);
            ScanColumnTypeSignals(table, findings);
            ScanFloatOrRealIndexKeyColumns(table, findings);
            ScanNoRecomputeStatistics(table, findings);
            ScanVariableLengthKeyColumnWidth(table, findings);
            ScanMergeableIncludeOnlyIndexes(table, findings);
            ScanColumnstoreOnDmlTargetTable(table, dmlTargetTables, findings);
            ScanMonotonicClusteredKeyMissingSequentialOptimization(table, findings);
        }

        ScanUnindexedForeignKeys(catalog, findings);

        return
        [
            .. findings
                .OrderBy(f => f.TableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.Kind)
                .ThenBy(f => f.IndexName, StringComparer.Ordinal),
        ];
    }

    private static void ScanHeapFindings(CatalogTable table, List<IndexDesignFinding> findings)
    {
        // A memory-optimized table has no on-disk heap/RID storage at all - the engine requires
        // at least one HASH or NONCLUSTERED (BW-tree) index and never produces a type=1 CLUSTERED
        // row for one, so "no clustered index" would misfire as "heap" for every such table.
        if (table.IsMemoryOptimized)
        {
            return;
        }

        var hasClusteredIndex = table.Indexes.Any(i => i.IsClustered);
        if (hasClusteredIndex)
        {
            return;
        }

        var activeNonclustered = table.Indexes.Where(i => !i.IsDisabled).ToList();
        if (activeNonclustered.Count == 0)
        {
            // A heap with ZERO indexes at all - a staging/bulk-load table, often a deliberate
            // design (docs/detection-checklist.md's own scoping note). Not this finding's target.
            return;
        }

        var nonclusteredPrimaryKey = activeNonclustered.FirstOrDefault(i => i.Kind == CatalogIndexKind.PrimaryKey);
        if (nonclusteredPrimaryKey is not null)
        {
            // The sharper sibling subsumes the general case - the PK's own index IS one of the
            // "nonclustered indexes present" that would otherwise also qualify, so only the
            // sharper finding fires, never both for the same underlying cause.
            findings.Add(new IndexDesignFinding(
                IndexDesignFindingKind.HeapWithNonclusteredPrimaryKey,
                table.QualifiedName,
                nonclusteredPrimaryKey.Name,
                $"'{table.QualifiedName}' has no clustered index anywhere - its own PRIMARY KEY constraint ('{nonclusteredPrimaryKey.Name ?? "<unnamed>"}') is declared NONCLUSTERED. Every nonclustered index on this table (including this one) points back to its base row with an 8-byte RID instead of the clustering key.",
                table.SourcePath,
                table.SourceLine));
            return;
        }

        var names = string.Join(", ", activeNonclustered.Select(i => i.Name ?? "<unnamed>"));
        findings.Add(new IndexDesignFinding(
            IndexDesignFindingKind.HeapWithNonclusteredIndexes,
            table.QualifiedName,
            activeNonclustered[0].Name,
            $"'{table.QualifiedName}' has no clustered index anywhere but carries {activeNonclustered.Count} nonclustered index(es) ({names}) - each one points back to its base row with an 8-byte RID instead of the clustering key, and that RID can change under heap maintenance (forwarded-row pointers).",
            table.SourcePath,
            table.SourceLine));
    }

    private static void ScanClusteringKeyQuality(
        CatalogTable table, Dictionary<string, string> defaultTextByColumn, List<IndexDesignFinding> findings)
    {
        // The genuine rowstore clustering KEY - never a clustered COLUMNSTORE index, which has no
        // traditional key/uniquifier concept at all (see CatalogIndex.IsClustered's own doc
        // comment on why IsClustered alone is the wrong guard here).
        var clusteredIndex = table.Indexes.FirstOrDefault(i => i.IsClustered && !i.IsColumnstore);
        if (clusteredIndex is null || clusteredIndex.KeyColumns.Count == 0)
        {
            return;
        }

        if (!clusteredIndex.IsUnique)
        {
            findings.Add(new IndexDesignFinding(
                IndexDesignFindingKind.NonUniqueClusteredIndex,
                table.QualifiedName,
                clusteredIndex.Name,
                $"'{table.QualifiedName}' clustered index '{clusteredIndex.Name ?? "<unnamed>"}' ({string.Join(", ", clusteredIndex.KeyColumns)}) is not unique - the engine adds a hidden 4-byte uniquifier to every duplicate-keyed row, widening the clustering key that every nonclustered index on this table also carries in its own leaf rows.",
                table.SourcePath,
                table.SourceLine));
        }

        var keyColumnTypes = clusteredIndex.KeyColumns.Select(table.FindColumn).ToList();
        var wideByColumnCount = clusteredIndex.KeyColumns.Count > WideClusteredKeyMaxColumns;

        int? totalBytes = 0;
        foreach (var column in keyColumnTypes)
        {
            var columnBytes = column?.Type is { } type ? EstimateColumnKeyBytes(type) : null;
            if (columnBytes is null)
            {
                // Never guess: if any key column's byte width can't be resolved, the byte-based
                // half of this check is dropped entirely rather than reporting a lower-bound
                // total that could understate (never overstate, since it's a lower bound - but a
                // silently partial number is still not "the total key width" this finding claims).
                totalBytes = null;
                break;
            }

            totalBytes += columnBytes;
        }

        var wideByBytes = totalBytes is { } bytes && bytes > WideClusteredKeyMaxBytes;
        if (wideByColumnCount || wideByBytes)
        {
            var bytesText = totalBytes is { } b ? $"{b} bytes" : "byte width unresolved for at least one key column";
            findings.Add(new IndexDesignFinding(
                IndexDesignFindingKind.WideClusteredKey,
                table.QualifiedName,
                clusteredIndex.Name,
                $"'{table.QualifiedName}' clustered index '{clusteredIndex.Name ?? "<unnamed>"}' has {clusteredIndex.KeyColumns.Count} key column(s) ({string.Join(", ", clusteredIndex.KeyColumns)}, {bytesText}) - every nonclustered index on this table carries a full copy of this key in every leaf row.",
                table.SourcePath,
                table.SourceLine,
                FindingConfidence.Medium));
        }

        var leadingColumnName = clusteredIndex.KeyColumns[0];
        var leadingColumn = table.FindColumn(leadingColumnName);
        if (leadingColumn?.Type?.Category != SqlTypeCategory.UniqueIdentifier)
        {
            return;
        }

        var key = DefaultKey(table.QualifiedName, leadingColumnName);
        if (!defaultTextByColumn.TryGetValue(key, out var defaultText) || !IsNewIdDefault(defaultText))
        {
            return;
        }

        findings.Add(new IndexDesignFinding(
            IndexDesignFindingKind.RandomClusteredKeyGuidDefault,
            table.QualifiedName,
            clusteredIndex.Name,
            $"'{table.QualifiedName}' clustered index '{clusteredIndex.Name ?? "<unnamed>"}' leads on '{leadingColumnName}', a uniqueidentifier column defaulted to NEWID() - genuinely random insert order into a clustered B-tree causes severe page splits and fragmentation. NEWSEQUENTIALID() avoids this and does not fire here.",
            table.SourcePath,
            table.SourceLine));
    }

    /// <summary>
    /// docs/detection-checklist.md §A "Duplicate and prefix-subsumed indexes". Only active
    /// (non-disabled), unfiltered, non-columnstore, non-empty-key indexes are ever compared - a
    /// filtered index's own predicate text isn't read by this catalog (only <see
    /// cref="CatalogIndex.IsFiltered"/> is), so two filtered indexes' definitions can never be
    /// confirmed equal here and are excluded rather than guessed about; a columnstore index has no
    /// ordered B-tree key the same way. <see cref="CatalogIndex.IsUnique"/> and
    /// <see cref="CatalogIndex.Kind"/> must both match too - the checklist's own precision guard,
    /// "a unique index and a non-unique index on the same keys are not the same object".
    /// </summary>
    private static void ScanDuplicateAndSubsumedIndexes(CatalogTable table, List<IndexDesignFinding> findings)
    {
        var candidates = table.Indexes
            .Where(i => !i.IsDisabled && !i.IsFiltered && !i.IsColumnstore && i.KeyColumns.Count > 0)
            .ToList();

        for (var i = 0; i < candidates.Count; i++)
        {
            for (var j = i + 1; j < candidates.Count; j++)
            {
                var a = candidates[i];
                var b = candidates[j];
                if (a.IsUnique != b.IsUnique || a.Kind != b.Kind)
                {
                    continue;
                }

                if (a.KeyColumns.Count == b.KeyColumns.Count)
                {
                    if (KeyColumnsEqual(a.KeyColumns, b.KeyColumns))
                    {
                        findings.Add(new IndexDesignFinding(
                            IndexDesignFindingKind.DuplicateIndex,
                            table.QualifiedName,
                            b.Name,
                            $"'{table.QualifiedName}' indexes '{a.Name ?? "<unnamed>"}' and '{b.Name ?? "<unnamed>"}' share the identical key list ({string.Join(", ", a.KeyColumns)}), the same uniqueness, and the same index kind - exact duplicates. One is pure write amplification and wasted space with zero query benefit over the other.",
                            table.SourcePath,
                            table.SourceLine));
                    }

                    continue;
                }

                // Whichever of the pair has the SHORTER key list is the candidate for being
                // subsumed by the longer one - order the comparison so it only fires once per
                // pair, in the "shorter is subsumed by longer" direction.
                var (shorter, longer) = a.KeyColumns.Count < b.KeyColumns.Count ? (a, b) : (b, a);
                if (IsProperPrefix(shorter.KeyColumns, longer.KeyColumns)
                    && shorter.IncludedColumns.All(c => longer.IncludedColumns.Contains(c, StringComparer.OrdinalIgnoreCase)))
                {
                    findings.Add(new IndexDesignFinding(
                        IndexDesignFindingKind.SubsumedIndex,
                        table.QualifiedName,
                        shorter.Name,
                        $"'{table.QualifiedName}' index '{shorter.Name ?? "<unnamed>"}' ({string.Join(", ", shorter.KeyColumns)}) is a leading-column prefix of '{longer.Name ?? "<unnamed>"}' ({string.Join(", ", longer.KeyColumns)}), with its own INCLUDE columns already covered by '{longer.Name ?? "<unnamed>"}' - '{shorter.Name ?? "<unnamed>"}' is redundant, since '{longer.Name ?? "<unnamed>"}' can serve every seek it could.",
                        table.SourcePath,
                        table.SourceLine));
                }
            }
        }
    }

    private static bool KeyColumnsEqual(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);

    /// <summary>True iff <paramref name="shorter"/> is a non-empty, strictly shorter, ordered prefix of <paramref name="longer"/>.</summary>
    private static bool IsProperPrefix(IReadOnlyList<string> shorter, IReadOnlyList<string> longer)
    {
        if (shorter.Count == 0 || shorter.Count >= longer.Count)
        {
            return false;
        }

        for (var k = 0; k < shorter.Count; k++)
        {
            if (!string.Equals(shorter[k], longer[k], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// docs/detection-checklist.md §A "Disabled and hypothetical indexes". Checks
    /// <see cref="CatalogIndex.IsHypothetical"/> first - Microsoft's own documentation states a
    /// hypothetical index always carries <see cref="CatalogIndex.IsDisabled"/> = true too, so
    /// checking disabled-ness first would misreport every hypothetical index as a plain disabled
    /// one instead.
    /// </summary>
    private static void ScanDisabledAndHypotheticalIndexes(CatalogTable table, List<IndexDesignFinding> findings)
    {
        foreach (var index in table.Indexes)
        {
            if (index.IsHypothetical)
            {
                findings.Add(new IndexDesignFinding(
                    IndexDesignFindingKind.HypotheticalIndex,
                    table.QualifiedName,
                    index.Name,
                    $"'{table.QualifiedName}' index '{index.Name ?? "<unnamed>"}' is hypothetical (sys.indexes.is_hypothetical = 1) - a Database Engine Tuning Advisor/missing-index-wizard artifact with no real data behind it, left over after an analysis session. Safe to drop.",
                    table.SourcePath,
                    table.SourceLine));
            }
            else if (index.IsDisabled)
            {
                findings.Add(new IndexDesignFinding(
                    IndexDesignFindingKind.DisabledIndex,
                    table.QualifiedName,
                    index.Name,
                    $"'{table.QualifiedName}' index '{index.Name ?? "<unnamed>"}' is disabled (ALTER INDEX ... DISABLE) - unusable by the engine until rebuilt, but still occupies catalog metadata and blocks a same-named CREATE INDEX.",
                    table.SourcePath,
                    table.SourceLine));
            }
        }
    }

    /// <summary>docs/detection-checklist.md §A "Over-indexing": many nonclustered indexes on one table, and any single index with too many key columns.</summary>
    private static void ScanOverIndexing(CatalogTable table, List<IndexDesignFinding> findings)
    {
        var activeNonclustered = table.Indexes.Where(i => !i.IsClustered && !i.IsDisabled).ToList();
        if (activeNonclustered.Count >= ManyNonclusteredIndexesThreshold)
        {
            findings.Add(new IndexDesignFinding(
                IndexDesignFindingKind.ManyNonclusteredIndexes,
                table.QualifiedName,
                IndexName: null,
                $"'{table.QualifiedName}' carries {activeNonclustered.Count} nonclustered indexes - each one is paid for on every INSERT/UPDATE/DELETE against this table. This does not identify which (if any) index is safe to drop - that needs production usage statistics this catalog-only pass cannot see.",
                table.SourcePath,
                table.SourceLine,
                FindingConfidence.Medium));
        }

        // Never re-reported for the table's own clustered key - WideClusteredKey already covers
        // that object at its own, tighter 3-column threshold; double-reporting the same physical
        // index under two kinds would be redundant noise, not two independent findings.
        foreach (var index in table.Indexes)
        {
            if (!index.IsClustered && !index.IsDisabled && !index.IsColumnstore
                && index.KeyColumns.Count >= ManyKeyColumnsThreshold)
            {
                findings.Add(new IndexDesignFinding(
                    IndexDesignFindingKind.ManyKeyColumnsIndex,
                    table.QualifiedName,
                    index.Name,
                    $"'{table.QualifiedName}' index '{index.Name ?? "<unnamed>"}' has {index.KeyColumns.Count} key columns ({string.Join(", ", index.KeyColumns)}) - every one is carried in every leaf-level lookup and update against this index.",
                    table.SourcePath,
                    table.SourceLine,
                    FindingConfidence.Medium));
            }
        }
    }

    /// <summary>
    /// docs/detection-checklist.md §A, the three "lower-precision, listed for completeness"
    /// table-shape signals: wide table, high nullable-column ratio, high string-column ratio. All
    /// three require at least <see cref="RatioChecksMinColumns"/> columns before either ratio
    /// check is even evaluated, so a trivial 2-column table can never trip a ratio threshold
    /// merely by chance.
    /// </summary>
    private static void ScanTableShape(CatalogTable table, List<IndexDesignFinding> findings)
    {
        var columnCount = table.Columns.Count;
        if (columnCount == 0)
        {
            return;
        }

        var nonLobBytes = 0;
        foreach (var column in table.Columns)
        {
            // A column whose type never resolved (LOB/MAX, sql_variant, or an unmapped
            // user-defined type) contributes nothing to this sum rather than a guessed byte count -
            // the reported total is always a safe lower bound, never an overstatement, so it is
            // still safe to fire once the resolved portion alone already clears the threshold.
            nonLobBytes += column.Type is { } type ? EstimateColumnKeyBytes(type) ?? 0 : 0;
        }

        if (columnCount >= WideTableMinColumns || nonLobBytes > WideTableMaxNonLobBytes)
        {
            findings.Add(new IndexDesignFinding(
                IndexDesignFindingKind.WideTable,
                table.QualifiedName,
                IndexName: null,
                $"'{table.QualifiedName}' has {columnCount} columns and an estimated {nonLobBytes} non-LOB bytes per row - a data-modeling signal (normalization, or separating hot/cold columns), not a specific, provable defect this pass can point at.",
                table.SourcePath,
                table.SourceLine,
                FindingConfidence.Low));
        }

        if (columnCount < RatioChecksMinColumns)
        {
            return;
        }

        var nullableCount = table.Columns.Count(c => c.IsNullable);
        var nullableRatio = (double)nullableCount / columnCount;
        if (nullableRatio >= HighNullableColumnRatioThreshold)
        {
            findings.Add(new IndexDesignFinding(
                IndexDesignFindingKind.HighNullableColumnRatio,
                table.QualifiedName,
                IndexName: null,
                $"'{table.QualifiedName}' has {nullableCount}/{columnCount} columns ({nullableRatio:P0}) nullable - often a sign of several optional sub-entities crammed into one table, though this pass cannot confirm that for any specific column here.",
                table.SourcePath,
                table.SourceLine,
                FindingConfidence.Low));
        }

        var stringCount = table.Columns.Count(c => c.Type?.IsStringFamily == true);
        var stringRatio = (double)stringCount / columnCount;
        if (stringRatio >= HighStringColumnRatioThreshold)
        {
            findings.Add(new IndexDesignFinding(
                IndexDesignFindingKind.HighStringColumnRatio,
                table.QualifiedName,
                IndexName: null,
                $"'{table.QualifiedName}' has {stringCount}/{columnCount} columns ({stringRatio:P0}) string-typed - often correlates with under-typed data (dates/numbers/enums stored as text with no CHECK/FK narrowing), though this pass cannot confirm that for any specific column here.",
                table.SourcePath,
                table.SourceLine,
                FindingConfidence.Low));
        }
    }

    /// <summary>
    /// docs/detection-checklist.md §A "Unindexed foreign key columns". Groups the flat, per-column-
    /// pair <see cref="DatabaseCatalog.ForeignKeys"/> list into one entry per real constraint (the
    /// same grouping <see cref="PartialCompositeForeignKeyJoinScanner.BuildCompositeForeignKeys"/>
    /// uses for composite FKs, generalized here to single-column FKs too, since a lone unindexed FK
    /// column is exactly as real a finding as a composite one). A constraint fires when NO active,
    /// unfiltered, non-columnstore index on the child (parent-side) table has this exact column set
    /// as its own leading key-column prefix - a composite-aware, order-tolerant-on-the-FK-side
    /// comparison (the underlying read order of <see cref="ForeignKeyRelationship"/> rows across one
    /// constraint is not guaranteed, so this deliberately compares column SETS rather than assuming
    /// a specific pair order), the same shape <see cref="NonUniqueUpdateSourceScanner"/>'s own
    /// uniqueness check already uses elsewhere in this codebase.
    /// </summary>
    private static void ScanUnindexedForeignKeys(DatabaseCatalog catalog, List<IndexDesignFinding> findings)
    {
        var constraints = catalog.ForeignKeys
            .GroupBy(fk => (fk.ConstraintName, fk.ParentTableQualifiedName), fk => fk, TupleComparer.Instance);

        foreach (var group in constraints)
        {
            var parentTable = catalog.Find(group.Key.ParentTableQualifiedName);
            if (parentTable is null)
            {
                // The FK's own parent table isn't in this catalog (an unresolvable cross-database
                // reference, or a table this scan otherwise skipped) - never guess at its indexes.
                continue;
            }

            var fkColumns = new HashSet<string>(group.Select(fk => fk.ParentColumnName), StringComparer.OrdinalIgnoreCase);
            var referencedTable = group.First().ReferencedTableQualifiedName;

            var hasLeadingIndex = parentTable.Indexes.Any(i =>
                !i.IsDisabled && !i.IsFiltered && !i.IsColumnstore
                && i.KeyColumns.Count >= fkColumns.Count
                && i.KeyColumns.Take(fkColumns.Count).All(c => fkColumns.Contains(c)));

            if (hasLeadingIndex)
            {
                continue;
            }

            findings.Add(new IndexDesignFinding(
                IndexDesignFindingKind.UnindexedForeignKey,
                parentTable.QualifiedName,
                IndexName: null,
                $"'{parentTable.QualifiedName}' foreign key '{group.Key.ConstraintName}' ({string.Join(", ", fkColumns)}) referencing '{referencedTable}' has no active index leading on its own column set - every parent-side DELETE/UPDATE forces a referential-integrity scan of this table, and every join along this relationship has no seek path.",
                parentTable.SourcePath,
                parentTable.SourceLine));
        }
    }

    /// <summary>Equality comparer for the <c>(ConstraintName, ParentTableQualifiedName)</c> GroupBy key above - ordinal-ignore-case on both components, matching every other qualified-name comparison in this codebase.</summary>
    private sealed class TupleComparer : IEqualityComparer<(string ConstraintName, string ParentTableQualifiedName)>
    {
        public static readonly TupleComparer Instance = new();

        public bool Equals((string ConstraintName, string ParentTableQualifiedName) x, (string ConstraintName, string ParentTableQualifiedName) y) =>
            string.Equals(x.ConstraintName, y.ConstraintName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.ParentTableQualifiedName, y.ParentTableQualifiedName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string ConstraintName, string ParentTableQualifiedName) obj) =>
            HashCode.Combine(
                obj.ConstraintName.ToUpperInvariant(),
                obj.ParentTableQualifiedName.ToUpperInvariant());
    }

    /// <summary>
    /// docs/detection-checklist.md §A "Filtered index whose filter columns are absent from its own
    /// key + include list". Only evaluated for an index with <see cref="CatalogIndex.IsFiltered"/>
    /// true and a non-null <see cref="CatalogIndex.FilterDefinition"/> (live-only - see that
    /// field's own doc comment). The filter text is reparsed as a WHERE search condition through
    /// the same throwaway-wrapper-statement technique <see cref="SchemaDependencyScanner"/> already
    /// uses for a CHECK constraint's own definition text - a filter's stored text is always a valid
    /// boolean predicate on its own (the engine itself stored it that way), so wrapping it under
    /// <c>WHERE</c> is always the right shape, unlike a computed-column/DEFAULT definition which
    /// wraps as a bare SELECT-list scalar expression instead. A filter this pass cannot parse is
    /// left unanalyzed entirely - never guessed at.
    /// </summary>
    private static void ScanFilteredIndexColumnCoverage(CatalogTable table, List<IndexDesignFinding> findings)
    {
        foreach (var index in table.Indexes)
        {
            if (!index.IsFiltered || index.FilterDefinition is not { } filterDefinition)
            {
                continue;
            }

            var filterColumns = TryExtractFilterColumnNames(filterDefinition);
            if (filterColumns is null || filterColumns.Count == 0)
            {
                continue;
            }

            var carriedColumns = new HashSet<string>(index.KeyColumns, StringComparer.OrdinalIgnoreCase);
            carriedColumns.UnionWith(index.IncludedColumns);

            var missing = filterColumns.Where(c => !carriedColumns.Contains(c)).ToList();
            if (missing.Count == 0)
            {
                continue;
            }

            findings.Add(new IndexDesignFinding(
                IndexDesignFindingKind.FilterColumnNotInIndex,
                table.QualifiedName,
                index.Name,
                $"'{table.QualifiedName}' filtered index '{index.Name ?? "<unnamed>"}' has filter '{filterDefinition}' referencing column(s) not in its own key/INCLUDE list: {string.Join(", ", missing)} - the engine can only substitute this index for a query whose own WHERE clause restates the filter predicate, and it cannot cheaply confirm that without reading those column(s) from the base table.",
                table.SourcePath,
                table.SourceLine));
        }
    }

    /// <summary>
    /// Reparses a <c>sys.indexes.filter_definition</c> string (e.g. <c>"([IsActive]=(1))"</c>) as a
    /// WHERE search condition and returns the distinct column names it references, or
    /// <see langword="null"/> if the text does not parse cleanly as one - the same "never guess"
    /// discipline every other reparse in this codebase follows.
    /// </summary>
    private static List<string>? TryExtractFilterColumnNames(string filterDefinition)
    {
        var result = SqlScriptParser.ParseText("filter-definition.sql", $"SELECT 1 WHERE {filterDefinition};");
        if (result.HasErrors
            || result.Fragment is not TSqlScript { Batches: [{ Statements: [SelectStatement { QueryExpression: QuerySpecification { WhereClause.SearchCondition: { } searchCondition } } ] }] })
        {
            return null;
        }

        var collector = new ColumnNameCollector();
        searchCondition.Accept(collector);
        return [.. collector.Names];
    }

    /// <summary>
    /// docs/detection-checklist.md full-archive practitioner sweep §E, "Filtered index whose
    /// predicate compares against a variable/parameter, not a literal" - reused by
    /// <see cref="TypedPredicateExtractor"/> (see <see cref="FilteredIndexParameterMismatchFinding"/>'s
    /// own doc comment for why that finding lives in its own type rather than here). The optimizer
    /// can only match a filtered index against a query filtering the SAME column with a LITERAL
    /// restating this exact filter, so only the simplest, unambiguous shape is extracted: a single
    /// <c>Column = Literal</c> equality (the shape <c>sys.indexes.filter_definition</c> renders a
    /// plain <c>WHERE Column = 'Value'</c> filter as, confirmed directly against the standing Docker
    /// oracle) - <see langword="null"/> for anything else (a multi-predicate filter, a non-equality
    /// operator, a filter against an expression rather than a bare column) rather than guessing at a
    /// looser shape this pass hasn't verified the optimizer's own matching rule for.
    /// </summary>
    public static (string ColumnName, string LiteralText)? TryExtractSimpleLiteralEqualityFilter(string filterDefinition)
    {
        var result = SqlScriptParser.ParseText("filter-definition.sql", $"SELECT 1 WHERE {filterDefinition};");
        if (result.HasErrors
            || result.Fragment is not TSqlScript { Batches: [{ Statements: [SelectStatement { QueryExpression: QuerySpecification { WhereClause.SearchCondition: { } searchCondition } }] }] })
        {
            return null;
        }

        // sys.indexes.filter_definition always wraps its own predicate in parentheses (e.g.
        // "([Status]='Active')", confirmed directly against the standing Docker oracle) - strip
        // any number of nested BooleanParenthesisExpression layers before matching the real shape
        // underneath, the same way a hand-typed filter with extra parens would still need to.
        var condition = searchCondition;
        while (condition is BooleanParenthesisExpression parenthesized)
        {
            condition = parenthesized.Expression;
        }

        if (condition is not BooleanComparisonExpression
            {
                ComparisonType: BooleanComparisonType.Equals,
                FirstExpression: ColumnReferenceExpression { MultiPartIdentifier.Identifiers: [.., { Value: { } columnName }] },
                SecondExpression: Literal literal,
            }
            || Rules.LiteralTextRenderer.Render(literal) is not { } literalText)
        {
            return null;
        }

        return (columnName, literalText);
    }

    private sealed class ColumnNameCollector : TSqlFragmentVisitor
    {
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier?.Identifiers is { Count: > 0 } identifiers)
            {
                Names.Add(identifiers[^1].Value);
            }

            base.ExplicitVisit(node);
        }
    }

    /// <summary>
    /// docs/detection-checklist.md §A "Deprecated LOB column types in the schema" - a plain
    /// column-type walk, no AST, mirroring <see cref="MaxTypedColumnScanner"/>'s own shape. Splits
    /// the checklist's single item into two kinds because they are not the same claim: <c>text</c>/
    /// <c>ntext</c>/<c>image</c> are a genuine functional deprecation (<see
    /// cref="IndexDesignFindingKind.DeprecatedLobColumnType"/>), <c>timestamp</c> vs. <c>rowversion</c>
    /// is a naming-only recommendation over the identical underlying type (<see
    /// cref="IndexDesignFindingKind.TimestampColumnNaming"/>) - see each kind's own doc comment.
    /// </summary>
    private static void ScanColumnTypeSignals(CatalogTable table, List<IndexDesignFinding> findings)
    {
        foreach (var column in table.Columns)
        {
            switch (column.Type?.Category)
            {
                case SqlTypeCategory.Text or SqlTypeCategory.NText or SqlTypeCategory.Image:
                    findings.Add(new IndexDesignFinding(
                        IndexDesignFindingKind.DeprecatedLobColumnType,
                        table.QualifiedName,
                        IndexName: null,
                        $"'{table.QualifiedName}.{column.Name}' is declared {column.Type}, formally deprecated by Microsoft since SQL Server 2005 in favor of the MAX-length equivalent - a future engine version may remove it entirely, and it already cannot be used in most string functions or a variable/parameter type in many contexts the MAX-length equivalent supports natively.",
                        table.SourcePath,
                        table.SourceLine));
                    break;

                case SqlTypeCategory.Timestamp:
                    findings.Add(new IndexDesignFinding(
                        IndexDesignFindingKind.TimestampColumnNaming,
                        table.QualifiedName,
                        IndexName: null,
                        $"'{table.QualifiedName}.{column.Name}' is declared timestamp - since SQL Server 2005, rowversion is a synonym for the exact same underlying type (not a functional deprecation, unlike text/ntext/image); Microsoft recommends the rowversion spelling for new development purely to avoid the name colliding with the unrelated SQL-standard TIMESTAMP datetime type.",
                        table.SourcePath,
                        table.SourceLine,
                        FindingConfidence.Low));
                    break;
            }
        }
    }

    /// <summary>
    /// docs/detection-checklist.md §A "float/real as an index key column" - the catalog-only half;
    /// see <see cref="FloatEqualityFinding"/> for the AST-level, sharper sibling (an actual equality
    /// predicate against such a column). Fires once per active (non-disabled) index that carries at
    /// least one float/real key column, listing every such column - not once per column, since the
    /// index itself is the unit a reader would act on (rebuild it on a different/additional key).
    /// </summary>
    private static void ScanFloatOrRealIndexKeyColumns(CatalogTable table, List<IndexDesignFinding> findings)
    {
        foreach (var index in table.Indexes)
        {
            if (index.IsDisabled || index.KeyColumns.Count == 0)
            {
                continue;
            }

            var floatKeyColumns = index.KeyColumns
                .Where(name => table.FindColumn(name)?.Type?.Category is SqlTypeCategory.Real or SqlTypeCategory.Float)
                .ToList();

            if (floatKeyColumns.Count == 0)
            {
                continue;
            }

            findings.Add(new IndexDesignFinding(
                IndexDesignFindingKind.FloatOrRealIndexKeyColumn,
                table.QualifiedName,
                index.Name,
                $"'{table.QualifiedName}' index '{index.Name ?? "<unnamed>"}' carries approximate (float/real) key column(s) {string.Join(", ", floatKeyColumns)} - IEEE-754 binary floating-point cannot represent every decimal value exactly, so an equality seek/comparison against it can silently miss a value a person would call 'the same number'.",
                table.SourcePath,
                table.SourceLine));
        }
    }

    /// <summary>
    /// docs/detection-checklist.md "DBA-script family sweep" §A "Statistics-object flags",
    /// <c>NO_RECOMPUTE</c> half - see <see cref="IndexDesignFindingKind.NoRecomputeStatistics"/>'s
    /// own doc comment for why the partitioned-incremental-statistics half is not shipped here.
    /// </summary>
    private static void ScanNoRecomputeStatistics(CatalogTable table, List<IndexDesignFinding> findings)
    {
        foreach (var stat in table.EffectiveStatistics.Where(s => s.NoRecompute))
        {
            findings.Add(new IndexDesignFinding(
                IndexDesignFindingKind.NoRecomputeStatistics,
                table.QualifiedName,
                stat.Name,
                $"'{table.QualifiedName}' statistics object '{stat.Name}' is marked NORECOMPUTE - the engine's automatic statistics maintenance never refreshes it, so its cardinality estimate silently drifts stale as the table's data changes.",
                table.SourcePath,
                table.SourceLine,
                Confidence: FindingConfidence.Medium));
        }
    }

    /// <summary>
    /// Confirmed directly against the standing Docker oracle (2026-08-18): the engine's own printed
    /// warning at CREATE INDEX time states this exact number, verbatim, for a clustered index /
    /// PRIMARY KEY / UNIQUE constraint's own key: "The maximum key length for a clustered index is
    /// 900 bytes."
    /// </summary>
    public const int ClusteredKeyLimitBytes = 900;

    /// <summary>
    /// Same oracle confirmation as <see cref="ClusteredKeyLimitBytes"/>, for a NONCLUSTERED index's
    /// own key: "The maximum key length for a nonclustered index is 1700 bytes." - a materially
    /// different, larger ceiling than the clustered case, so <see
    /// cref="ScanVariableLengthKeyColumnWidth"/> checks <see cref="Catalog.CatalogIndex.IsClustered"/>
    /// per index rather than assuming one flat number for every index type.
    /// </summary>
    public const int NonclusteredKeyLimitBytes = 1700;

    /// <summary>
    /// docs/detection-checklist.md full-archive practitioner sweep §E, "Column too wide to ever be
    /// an index key" - see <see cref="IndexDesignFindingKind.VariableLengthKeyColumnExceedsKeyLimit"/>'s
    /// own doc comment for the full oracle-verified correction (fixed-length excluded as an
    /// already-hard-DDL-error case; variable-length only, since CREATE INDEX there only warns and
    /// the real failure is deferred to a future INSERT/UPDATE). Only active (non-disabled) indexes
    /// are checked - a disabled index already has its own, separate finding
    /// (<see cref="IndexDesignFindingKind.DisabledIndex"/>) and cannot fail an INSERT today since it
    /// serves no query and enforces nothing.
    /// </summary>
    private static void ScanVariableLengthKeyColumnWidth(CatalogTable table, List<IndexDesignFinding> findings)
    {
        foreach (var index in table.Indexes)
        {
            if (index.IsDisabled || index.IsColumnstore || index.KeyColumns.Count == 0)
            {
                continue;
            }

            var limit = index.IsClustered ? ClusteredKeyLimitBytes : NonclusteredKeyLimitBytes;

            foreach (var columnName in index.KeyColumns)
            {
                var column = table.FindColumn(columnName);
                if (column?.Type is not { IsMax: false } type
                    || type.Category is not (SqlTypeCategory.VarChar or SqlTypeCategory.NVarChar or SqlTypeCategory.VarBinary)
                    || EstimateColumnKeyBytes(type) is not { } declaredBytes
                    || declaredBytes <= limit)
                {
                    continue;
                }

                findings.Add(new IndexDesignFinding(
                    IndexDesignFindingKind.VariableLengthKeyColumnExceedsKeyLimit,
                    table.QualifiedName,
                    index.Name,
                    $"'{table.QualifiedName}' index '{index.Name ?? "<unnamed>"}' key column '{columnName}' is declared {type} - a {declaredBytes}-byte maximum width, over the engine's {limit}-byte {(index.IsClustered ? "clustered" : "nonclustered")} key limit. CREATE INDEX only warns, it does not fail - the first INSERT/UPDATE that actually stores a value long enough to exceed {limit} bytes fails at that moment instead, silently until then.",
                    table.SourcePath,
                    table.SourceLine));
            }
        }
    }

    /// <summary>
    /// docs/detection-checklist.md second full-archive practitioner sweep §G, "Indexes sharing an
    /// identical key-column list and sort direction but with different, non-overlapping INCLUDE
    /// sets" - see <see cref="IndexDesignFindingKind.MergeableIndexesDifferingIncludeOnly"/>'s own
    /// doc comment for the precision guards. Candidates are the same active/unfiltered/non-
    /// columnstore/non-empty-key population <see cref="ScanDuplicateAndSubsumedIndexes"/> already
    /// uses.
    /// </summary>
    private static void ScanMergeableIncludeOnlyIndexes(CatalogTable table, List<IndexDesignFinding> findings)
    {
        var candidates = table.Indexes
            .Where(i => !i.IsDisabled && !i.IsFiltered && !i.IsColumnstore && i.KeyColumns.Count > 0 && i.KeyColumnIsDescending.Count == i.KeyColumns.Count)
            .ToList();

        for (var i = 0; i < candidates.Count; i++)
        {
            for (var j = i + 1; j < candidates.Count; j++)
            {
                var a = candidates[i];
                var b = candidates[j];
                if (a.IsUnique != b.IsUnique || a.Kind != b.Kind || a.KeyColumns.Count != b.KeyColumns.Count)
                {
                    continue;
                }

                if (!KeyColumnsEqual(a.KeyColumns, b.KeyColumns) || !a.KeyColumnIsDescending.SequenceEqual(b.KeyColumnIsDescending))
                {
                    continue;
                }

                var aIncluded = new HashSet<string>(a.IncludedColumns, StringComparer.OrdinalIgnoreCase);
                var bIncluded = new HashSet<string>(b.IncludedColumns, StringComparer.OrdinalIgnoreCase);

                // Identical INCLUDE sets is DuplicateIndex's own territory, not this kind's - and a
                // subset relationship either way means one index is already SubsumedIndex-eligible
                // (same key list counts as a trivial "prefix" of itself) - only a genuine
                // non-overlapping divergence on BOTH sides belongs here.
                if (aIncluded.SetEquals(bIncluded) || aIncluded.IsSubsetOf(bIncluded) || bIncluded.IsSubsetOf(aIncluded))
                {
                    continue;
                }

                var union = string.Join(", ", aIncluded.Concat(bIncluded).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).Distinct(StringComparer.OrdinalIgnoreCase));
                findings.Add(new IndexDesignFinding(
                    IndexDesignFindingKind.MergeableIndexesDifferingIncludeOnly,
                    table.QualifiedName,
                    b.Name,
                    $"'{table.QualifiedName}' indexes '{a.Name ?? "<unnamed>"}' and '{b.Name ?? "<unnamed>"}' share the identical key list ({string.Join(", ", a.KeyColumns)}) and sort direction but carry different, non-overlapping INCLUDE columns ('{a.Name ?? "<unnamed>"}': {string.Join(", ", a.IncludedColumns)}; '{b.Name ?? "<unnamed>"}': {string.Join(", ", b.IncludedColumns)}) - mergeable into one index carrying the union ({union}) at no seek cost to either original query, for less write/storage overhead than carrying both.",
                    table.SourcePath,
                    table.SourceLine));
            }
        }
    }

    /// <summary>
    /// docs/detection-checklist.md full-archive practitioner sweep §E, "Columnstore index present on
    /// a table that is also a live DML target of transactional code" - see
    /// <see cref="IndexDesignFindingKind.ColumnstoreIndexOnDmlTargetTable"/>'s own doc comment for
    /// the oracle-confirmed rowgroup-lock mechanism and the explicit workload-dependent scope limit.
    /// </summary>
    private static void ScanColumnstoreOnDmlTargetTable(CatalogTable table, IReadOnlySet<string>? dmlTargetTables, List<IndexDesignFinding> findings)
    {
        if (dmlTargetTables is null || !dmlTargetTables.Contains(table.QualifiedName))
        {
            return;
        }

        var columnstoreIndex = table.Indexes.FirstOrDefault(i => !i.IsDisabled && i.IsColumnstore);
        if (columnstoreIndex is null)
        {
            return;
        }

        findings.Add(new IndexDesignFinding(
            IndexDesignFindingKind.ColumnstoreIndexOnDmlTargetTable,
            table.QualifiedName,
            columnstoreIndex.Name,
            $"'{table.QualifiedName}' carries a columnstore index ('{columnstoreIndex.Name ?? "<unnamed>"}') and is also a direct INSERT/UPDATE/DELETE/MERGE target elsewhere in this codebase - lock escalation on a columnstore index happens at ROWGROUP granularity, not row granularity, so a single-row write inside an explicit transaction can block unrelated concurrent access to every other row sharing that rowgroup. Structural risk flag only: whether contention actually occurs is workload-dependent (concurrent access pattern, rowgroup size) and out of reach for this static pass.",
            table.SourcePath,
            table.SourceLine,
            FindingConfidence.Medium));
    }

    /// <summary>
    /// docs/detection-checklist.md second full-archive practitioner sweep §G, "Monotonically
    /// increasing clustered key ... with no OPTIMIZE_FOR_SEQUENTIAL_KEY" - see
    /// <see cref="IndexDesignFindingKind.MonotonicClusteredKeyMissingSequentialOptimization"/>'s own
    /// doc comment for the oracle-confirmed feature/flag verification and the explicit
    /// IDENTITY-only scope limit.
    /// </summary>
    private static void ScanMonotonicClusteredKeyMissingSequentialOptimization(CatalogTable table, List<IndexDesignFinding> findings)
    {
        var clusteredIndex = table.Indexes.FirstOrDefault(i => i.IsClustered && !i.IsColumnstore && !i.IsDisabled);
        if (clusteredIndex is null || clusteredIndex.KeyColumns.Count == 0 || clusteredIndex.OptimizeForSequentialKey)
        {
            return;
        }

        var leadingColumn = table.FindColumn(clusteredIndex.KeyColumns[0]);
        if (leadingColumn is not { IsIdentity: true, IdentityIncrement: > 0 })
        {
            return;
        }

        findings.Add(new IndexDesignFinding(
            IndexDesignFindingKind.MonotonicClusteredKeyMissingSequentialOptimization,
            table.QualifiedName,
            clusteredIndex.Name,
            $"'{table.QualifiedName}' clustered index '{clusteredIndex.Name ?? "<unnamed>"}' leads on '{leadingColumn.Name}', an always-ascending IDENTITY column, with OPTIMIZE_FOR_SEQUENTIAL_KEY not enabled - every insert lands on the same trailing page, so concurrent inserts can serialize on that page's latch. Structural risk flag only: whether this actually causes contention depends on concurrent insert rate, which is workload data out of reach for this static pass.",
            table.SourcePath,
            table.SourceLine,
            FindingConfidence.Medium));
    }

    /// <summary>
    /// Best-effort physical storage size in bytes for one clustered-key column, from the same
    /// <see cref="SqlType"/> facets this catalog already resolves - the checklist's own "computed
    /// from the column types we already have," not a fresh catalog read.
    /// <see langword="null"/> for a type this pass cannot size confidently (a MAX-length string/
    /// binary - which SQL Server refuses as a key column anyway - <c>sql_variant</c>, or an
    /// unresolved/user-defined type), never a guessed number.
    /// </summary>
    public static int? EstimateColumnKeyBytes(SqlType type)
    {
        if (type.IsMax)
        {
            return null;
        }

        return type.Category switch
        {
            SqlTypeCategory.TinyInt or SqlTypeCategory.Bit => 1,
            SqlTypeCategory.SmallInt => 2,
            SqlTypeCategory.Int => 4,
            SqlTypeCategory.BigInt => 8,
            SqlTypeCategory.SmallMoney => 4,
            SqlTypeCategory.Money => 8,
            SqlTypeCategory.Real => 4,
            // Plain FLOAT with no declared precision resolves to float(53) (8 bytes) - the
            // engine's own default; float(n) for n <= 24 is stored as 4 bytes.
            SqlTypeCategory.Float => type.Precision is { } p && p <= 24 ? 4 : 8,
            SqlTypeCategory.Decimal => EstimateDecimalBytes(type.Precision),
            SqlTypeCategory.Date => 3,
            SqlTypeCategory.SmallDateTime => 4,
            SqlTypeCategory.DateTime => 8,
            SqlTypeCategory.Time => EstimateTemporalScaledBytes(type.Scale, baseBytes: 3),
            SqlTypeCategory.DateTime2 => EstimateTemporalScaledBytes(type.Scale, baseBytes: 6),
            SqlTypeCategory.DateTimeOffset => EstimateTemporalScaledBytes(type.Scale, baseBytes: 8),
            SqlTypeCategory.UniqueIdentifier => 16,
            SqlTypeCategory.Timestamp => 8,
            SqlTypeCategory.Char or SqlTypeCategory.VarChar or SqlTypeCategory.Binary or SqlTypeCategory.VarBinary =>
                type.Length,
            SqlTypeCategory.NChar or SqlTypeCategory.NVarChar => type.Length is { } len ? len * 2 : null,
            _ => null,
        };
    }

    private static int EstimateDecimalBytes(int? precision) => precision switch
    {
        <= 9 => 5,
        <= 19 => 9,
        <= 28 => 13,
        _ => 17,
    };

    private static int EstimateTemporalScaledBytes(int? scale, int baseBytes) => scale switch
    {
        <= 2 => baseBytes,
        <= 4 => baseBytes + 1,
        _ => baseBytes + 2,
    };

    /// <summary>
    /// True iff <paramref name="definitionText"/> (a <c>sys.default_constraints.definition</c>
    /// string like <c>"(newid())"</c>) is exactly a <c>NEWID()</c> call, once whitespace and
    /// parentheses are stripped - never a substring match, which would also match inside
    /// <c>"(newsequentialid())"</c> if done carelessly (it does not here: after stripping,
    /// "newid" vs "newsequentialid" are compared for exact equality, not containment).
    /// </summary>
    private static bool IsNewIdDefault(string? definitionText)
    {
        if (definitionText is null)
        {
            return false;
        }

        Span<char> buffer = stackalloc char[definitionText.Length];
        var length = 0;
        foreach (var c in definitionText)
        {
            if (c is '(' or ')' || char.IsWhiteSpace(c))
            {
                continue;
            }

            buffer[length++] = char.ToLowerInvariant(c);
        }

        return buffer[..length].SequenceEqual("newid");
    }

    private static string DefaultKey(string tableQualifiedName, string columnName) =>
        $"{tableQualifiedName}{columnName}";
}
