using SilentScan.Core.Catalog;

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

    public static IReadOnlyList<IndexDesignFinding> Scan(DatabaseCatalog catalog)
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
