using SilentScan.Core.Catalog;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Catalog-only pass for the five <see cref="IndexDesignFindingKind"/> members - see
/// <see cref="IndexDesignFinding"/>'s own doc comment for the full scope/precision story. Walks
/// <see cref="DatabaseCatalog.Tables"/> once, no AST, no query site involved; live-mode only
/// because <see cref="CatalogIndex.IsClustered"/> is live-only (see its own doc comment) - never
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
        }

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
