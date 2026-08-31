using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class IndexDesignScanner
{
    private const string UnnamedIndexPlaceholder = "<unnamed>";

    public static IReadOnlyList<IndexDesignFinding> Scan(DatabaseCatalog catalog, IReadOnlySet<string>? dmlTargetTables = null, IScanStage? stage = null)
    {
        var defaultTextByColumn = new Dictionary<string, string>(catalog.IdentifierComparer);
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
            stage?.Advance(currentItem: table.QualifiedName);

            if (table.Kind != CatalogTableKind.Table)
            {
                continue;
            }

            ScanHeapFindings(table, findings);
            ScanClusteringKeyQuality(table, defaultTextByColumn, catalog.IdentifierComparer, findings);
            ScanDuplicateAndSubsumedIndexes(table, catalog.IdentifierComparer, findings);
            ScanDisabledAndHypotheticalIndexes(table, findings);
            ScanFilteredIndexColumnCoverage(table, catalog.CompatibilityLevel, catalog.IdentifierComparer, findings);
            ScanColumnTypeSignals(table, findings);
            ScanFloatOrRealIndexKeyColumns(table, catalog.IdentifierComparer, findings);
            ScanNoRecomputeStatistics(table, findings);
            ScanVariableLengthKeyColumnWidth(table, catalog.IdentifierComparer, findings);
            ScanMergeableIncludeOnlyIndexes(table, catalog.IdentifierComparer, findings);
            ScanColumnstoreOnDmlTargetTable(table, dmlTargetTables, findings);
            ScanMonotonicClusteredKeyMissingSequentialOptimization(table, catalog.IdentifierComparer, findings);
            ScanNonAlignedPartitionedIndex(table, catalog.IdentifierComparer, findings);
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

            return;
        }

        var nonclusteredPrimaryKey = activeNonclustered.FirstOrDefault(i => i.Kind == CatalogIndexKind.PrimaryKey);
        if (nonclusteredPrimaryKey is not null)
        {

            findings.Add(new IndexDesignFinding(
                IndexDesignFindingKind.HeapWithNonclusteredPrimaryKey,
                table.QualifiedName,
                nonclusteredPrimaryKey.Name,
                $"'{table.QualifiedName}' has no clustered index anywhere - its own PRIMARY KEY constraint ('{nonclusteredPrimaryKey.Name ?? UnnamedIndexPlaceholder}') is declared NONCLUSTERED. Every nonclustered index on this table (including this one) points back to its base row with an 8-byte RID instead of the clustering key.",
                table.SourcePath,
                table.SourceLine));
            return;
        }

        var names = string.Join(", ", activeNonclustered.Select(i => i.Name ?? UnnamedIndexPlaceholder));
        findings.Add(new IndexDesignFinding(
            IndexDesignFindingKind.HeapWithNonclusteredIndexes,
            table.QualifiedName,
            activeNonclustered[0].Name,
            $"'{table.QualifiedName}' has no clustered index anywhere but carries {activeNonclustered.Count} nonclustered index(es) ({names}) - each one points back to its base row with an 8-byte RID instead of the clustering key, and that RID can change under heap maintenance (forwarded-row pointers).",
            table.SourcePath,
            table.SourceLine));
    }

    private static void ScanClusteringKeyQuality(
        CatalogTable table, Dictionary<string, string> defaultTextByColumn, StringComparer identifierComparer, List<IndexDesignFinding> findings)
    {

        var clusteredIndex = table.Indexes.FirstOrDefault(i => i.IsClustered && !i.IsColumnstore);
        if (clusteredIndex is null || clusteredIndex.KeyColumns.Count == 0)
        {
            return;
        }

        CheckNonUniqueClusteredIndex(table, clusteredIndex, findings);
        CheckRandomClusteredKeyGuidDefault(table, clusteredIndex, defaultTextByColumn, identifierComparer, findings);
    }

    private static void CheckNonUniqueClusteredIndex(CatalogTable table, CatalogIndex clusteredIndex, List<IndexDesignFinding> findings)
    {
        if (clusteredIndex.IsUnique)
        {
            return;
        }

        findings.Add(new IndexDesignFinding(
            IndexDesignFindingKind.NonUniqueClusteredIndex,
            table.QualifiedName,
            clusteredIndex.Name,
            $"'{table.QualifiedName}' clustered index '{clusteredIndex.Name ?? UnnamedIndexPlaceholder}' ({string.Join(", ", clusteredIndex.KeyColumns)}) is not unique - the engine adds a hidden 4-byte uniquifier to every duplicate-keyed row, widening the clustering key that every nonclustered index on this table also carries in its own leaf rows.",
            table.SourcePath,
            table.SourceLine));
    }

    private static void CheckRandomClusteredKeyGuidDefault(
        CatalogTable table, CatalogIndex clusteredIndex, Dictionary<string, string> defaultTextByColumn, StringComparer identifierComparer,
        List<IndexDesignFinding> findings)
    {
        var leadingColumnName = clusteredIndex.KeyColumns[0];
        var leadingColumn = table.FindColumn(leadingColumnName, identifierComparer);
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
            $"'{table.QualifiedName}' clustered index '{clusteredIndex.Name ?? UnnamedIndexPlaceholder}' leads on '{leadingColumnName}', a uniqueidentifier column defaulted to NEWID() - genuinely random insert order into a clustered B-tree causes severe page splits and fragmentation. NEWSEQUENTIALID() avoids this and does not fire here.",
            table.SourcePath,
            table.SourceLine));
    }

    private static void ScanDuplicateAndSubsumedIndexes(CatalogTable table, StringComparer identifierComparer, List<IndexDesignFinding> findings)
    {
        var candidates = table.Indexes
            .Where(i => !i.IsDisabled && !i.IsFiltered && !i.IsColumnstore && i.KeyColumns.Count > 0)
            .ToList();

        for (var i = 0; i < candidates.Count; i++)
        {
            for (var j = i + 1; j < candidates.Count; j++)
            {
                CompareIndexPairForDuplicateOrSubsumed(table, candidates[i], candidates[j], identifierComparer, findings);
            }
        }
    }

    private static void CompareIndexPairForDuplicateOrSubsumed(
        CatalogTable table, CatalogIndex a, CatalogIndex b, StringComparer identifierComparer, List<IndexDesignFinding> findings)
    {
        if (a.IsUnique != b.IsUnique || a.Kind != b.Kind)
        {
            return;
        }

        if (a.KeyColumns.Count == b.KeyColumns.Count)
        {
            if (KeyColumnsEqual(a.KeyColumns, b.KeyColumns, identifierComparer))
            {
                findings.Add(new IndexDesignFinding(
                    IndexDesignFindingKind.DuplicateIndex,
                    table.QualifiedName,
                    b.Name,
                    $"'{table.QualifiedName}' indexes '{a.Name ?? UnnamedIndexPlaceholder}' and '{b.Name ?? UnnamedIndexPlaceholder}' share the identical key list ({string.Join(", ", a.KeyColumns)}), the same uniqueness, and the same index kind - exact duplicates. One is pure write amplification and wasted space with zero query benefit over the other.",
                    table.SourcePath,
                    table.SourceLine));
            }

            return;
        }

        var (shorter, longer) = a.KeyColumns.Count < b.KeyColumns.Count ? (a, b) : (b, a);
        if (IsProperPrefix(shorter.KeyColumns, longer.KeyColumns, identifierComparer)
            && shorter.IncludedColumns.All(c => longer.IncludedColumns.Contains(c, identifierComparer)))
        {
            findings.Add(new IndexDesignFinding(
                IndexDesignFindingKind.SubsumedIndex,
                table.QualifiedName,
                shorter.Name,
                $"'{table.QualifiedName}' index '{shorter.Name ?? UnnamedIndexPlaceholder}' ({string.Join(", ", shorter.KeyColumns)}) is a leading-column prefix of '{longer.Name ?? UnnamedIndexPlaceholder}' ({string.Join(", ", longer.KeyColumns)}), with its own INCLUDE columns already covered by '{longer.Name ?? UnnamedIndexPlaceholder}' - '{shorter.Name ?? UnnamedIndexPlaceholder}' is redundant, since '{longer.Name ?? UnnamedIndexPlaceholder}' can serve every seek it could.",
                table.SourcePath,
                table.SourceLine));
        }
    }

    private static bool KeyColumnsEqual(IReadOnlyList<string> a, IReadOnlyList<string> b, StringComparer identifierComparer) =>
        a.SequenceEqual(b, identifierComparer);

    private static bool IsProperPrefix(IReadOnlyList<string> shorter, IReadOnlyList<string> longer, StringComparer identifierComparer)
    {
        if (shorter.Count == 0 || shorter.Count >= longer.Count)
        {
            return false;
        }

        for (var k = 0; k < shorter.Count; k++)
        {
            if (!identifierComparer.Equals(shorter[k], longer[k]))
            {
                return false;
            }
        }

        return true;
    }

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
                    $"'{table.QualifiedName}' index '{index.Name ?? UnnamedIndexPlaceholder}' is hypothetical (sys.indexes.is_hypothetical = 1) - a Database Engine Tuning Advisor/missing-index-wizard artifact with no real data behind it, left over after an analysis session. Safe to drop.",
                    table.SourcePath,
                    table.SourceLine));
            }
            else if (index.IsDisabled)
            {
                findings.Add(new IndexDesignFinding(
                    IndexDesignFindingKind.DisabledIndex,
                    table.QualifiedName,
                    index.Name,
                    $"'{table.QualifiedName}' index '{index.Name ?? UnnamedIndexPlaceholder}' is disabled (ALTER INDEX ... DISABLE) - unusable by the engine until rebuilt, but still occupies catalog metadata and blocks a same-named CREATE INDEX.",
                    table.SourcePath,
                    table.SourceLine));
            }
        }
    }

    private static void ScanUnindexedForeignKeys(DatabaseCatalog catalog, List<IndexDesignFinding> findings)
    {
        var constraints = catalog.ForeignKeys
            .GroupBy(fk => (fk.ConstraintName, fk.ParentTableQualifiedName), fk => fk, new TupleComparer(catalog.IdentifierComparer));

        foreach (var group in constraints)
        {
            var parentTable = catalog.Find(group.Key.ParentTableQualifiedName);
            if (parentTable is null)
            {

                continue;
            }

            var fkColumns = new HashSet<string>(group.Select(fk => fk.ParentColumnName), catalog.IdentifierComparer);
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

    private sealed class TupleComparer(StringComparer identifierComparer) : IEqualityComparer<(string ConstraintName, string ParentTableQualifiedName)>
    {
        public bool Equals((string ConstraintName, string ParentTableQualifiedName) x, (string ConstraintName, string ParentTableQualifiedName) y) =>
            identifierComparer.Equals(x.ConstraintName, y.ConstraintName)
            && identifierComparer.Equals(x.ParentTableQualifiedName, y.ParentTableQualifiedName);

        public int GetHashCode((string ConstraintName, string ParentTableQualifiedName) obj) =>
            HashCode.Combine(
                identifierComparer.GetHashCode(obj.ConstraintName),
                identifierComparer.GetHashCode(obj.ParentTableQualifiedName));
    }

    private static void ScanFilteredIndexColumnCoverage(
        CatalogTable table, int? compatibilityLevel, StringComparer identifierComparer, List<IndexDesignFinding> findings)
    {
        foreach (var index in table.Indexes)
        {
            if (!index.IsFiltered || index.FilterDefinition is not { } filterDefinition)
            {
                continue;
            }

            var filterColumns = TryExtractFilterColumnNames(filterDefinition, compatibilityLevel);
            if (filterColumns is null || filterColumns.Count == 0)
            {
                continue;
            }

            var carriedColumns = new HashSet<string>(index.KeyColumns, identifierComparer);
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
                $"'{table.QualifiedName}' filtered index '{index.Name ?? UnnamedIndexPlaceholder}' has filter '{filterDefinition}' referencing column(s) not in its own key/INCLUDE list: {string.Join(", ", missing)} - the engine can only substitute this index for a query whose own WHERE clause restates the filter predicate, and it cannot cheaply confirm that without reading those column(s) from the base table.",
                table.SourcePath,
                table.SourceLine));
        }
    }

    private static List<string>? TryExtractFilterColumnNames(string filterDefinition, int? compatibilityLevel)
    {
        var result = SqlScriptParser.ParseText("filter-definition.sql", $"SELECT 1 WHERE {filterDefinition};", initialQuotedIdentifiers: true, compatibilityLevel);
        if (result.HasErrors
            || result.Fragment is not TSqlScript { Batches: [{ Statements: [SelectStatement { QueryExpression: QuerySpecification { WhereClause.SearchCondition: { } searchCondition } } ] }] })
        {
            return null;
        }

        var collector = new ColumnNameCollector();
        searchCondition.Accept(collector);
        return [.. collector.Names];
    }

    public static (string ColumnName, string LiteralText)? TryExtractSimpleLiteralEqualityFilter(string filterDefinition, int? compatibilityLevel = null)
    {
        var result = SqlScriptParser.ParseText("filter-definition.sql", $"SELECT 1 WHERE {filterDefinition};", initialQuotedIdentifiers: true, compatibilityLevel);
        if (result.HasErrors
            || result.Fragment is not TSqlScript { Batches: [{ Statements: [SelectStatement { QueryExpression: QuerySpecification { WhereClause.SearchCondition: { } searchCondition } }] }] })
        {
            return null;
        }

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

    private static void ScanFloatOrRealIndexKeyColumns(CatalogTable table, StringComparer identifierComparer, List<IndexDesignFinding> findings)
    {
        foreach (var index in table.Indexes)
        {
            if (index.IsDisabled || index.KeyColumns.Count == 0)
            {
                continue;
            }

            var floatKeyColumns = index.KeyColumns
                .Where(name => table.FindColumn(name, identifierComparer)?.Type?.Category is SqlTypeCategory.Real or SqlTypeCategory.Float)
                .ToList();

            if (floatKeyColumns.Count == 0)
            {
                continue;
            }

            findings.Add(new IndexDesignFinding(
                IndexDesignFindingKind.FloatOrRealIndexKeyColumn,
                table.QualifiedName,
                index.Name,
                $"'{table.QualifiedName}' index '{index.Name ?? UnnamedIndexPlaceholder}' carries approximate (float/real) key column(s) {string.Join(", ", floatKeyColumns)} - IEEE-754 binary floating-point cannot represent every decimal value exactly, so an equality seek/comparison against it can silently miss a value a person would call 'the same number'.",
                table.SourcePath,
                table.SourceLine));
        }
    }

    private static void ScanNoRecomputeStatistics(CatalogTable table, List<IndexDesignFinding> findings) =>
        findings.AddRange(table.EffectiveStatistics.Where(s => s.NoRecompute).Select(stat => new IndexDesignFinding(
            IndexDesignFindingKind.NoRecomputeStatistics,
            table.QualifiedName,
            stat.Name,
            $"'{table.QualifiedName}' statistics object '{stat.Name}' is marked NORECOMPUTE - the engine's automatic statistics maintenance never refreshes it, so its cardinality estimate silently drifts stale as the table's data changes.",
            table.SourcePath,
            table.SourceLine,
            Confidence: FindingConfidence.Medium)));

    public const int ClusteredKeyLimitBytes = 900;

    public const int NonclusteredKeyLimitBytes = 1700;

    private static void ScanVariableLengthKeyColumnWidth(CatalogTable table, StringComparer identifierComparer, List<IndexDesignFinding> findings)
    {
        foreach (var index in table.Indexes)
        {
            if (index.IsDisabled || index.IsColumnstore || index.KeyColumns.Count == 0)
            {
                continue;
            }

            CheckVariableLengthKeyColumnWidth(table, index, identifierComparer, findings);
        }
    }

    private static void CheckVariableLengthKeyColumnWidth(
        CatalogTable table, CatalogIndex index, StringComparer identifierComparer, List<IndexDesignFinding> findings)
    {
        var limit = index.IsClustered ? ClusteredKeyLimitBytes : NonclusteredKeyLimitBytes;

        foreach (var columnName in index.KeyColumns)
        {
            var column = table.FindColumn(columnName, identifierComparer);
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
                $"'{table.QualifiedName}' index '{index.Name ?? UnnamedIndexPlaceholder}' key column '{columnName}' is declared {type} - a {declaredBytes}-byte maximum width, over the engine's {limit}-byte {(index.IsClustered ? "clustered" : "nonclustered")} key limit. CREATE INDEX only warns, it does not fail - the first INSERT/UPDATE that actually stores a value long enough to exceed {limit} bytes fails at that moment instead, silently until then.",
                table.SourcePath,
                table.SourceLine));
        }
    }

    private static void ScanMergeableIncludeOnlyIndexes(CatalogTable table, StringComparer identifierComparer, List<IndexDesignFinding> findings)
    {
        var candidates = table.Indexes
            .Where(i => !i.IsDisabled && !i.IsFiltered && !i.IsColumnstore && i.KeyColumns.Count > 0 && i.KeyColumnIsDescending.Count == i.KeyColumns.Count)
            .ToList();

        for (var i = 0; i < candidates.Count; i++)
        {
            for (var j = i + 1; j < candidates.Count; j++)
            {
                CompareIndexPairForMergeableIncludeOnly(table, candidates[i], candidates[j], identifierComparer, findings);
            }
        }
    }

    private static void CompareIndexPairForMergeableIncludeOnly(
        CatalogTable table, CatalogIndex a, CatalogIndex b, StringComparer identifierComparer, List<IndexDesignFinding> findings)
    {
        if (a.IsUnique != b.IsUnique || a.Kind != b.Kind || a.KeyColumns.Count != b.KeyColumns.Count)
        {
            return;
        }

        if (!KeyColumnsEqual(a.KeyColumns, b.KeyColumns, identifierComparer) || !a.KeyColumnIsDescending.SequenceEqual(b.KeyColumnIsDescending))
        {
            return;
        }

        var aIncluded = new HashSet<string>(a.IncludedColumns, identifierComparer);
        var bIncluded = new HashSet<string>(b.IncludedColumns, identifierComparer);

        if (aIncluded.SetEquals(bIncluded) || aIncluded.IsSubsetOf(bIncluded) || bIncluded.IsSubsetOf(aIncluded))
        {
            return;
        }

        var union = string.Join(", ", aIncluded.Concat(bIncluded).OrderBy(c => c, identifierComparer).Distinct(identifierComparer));
        findings.Add(new IndexDesignFinding(
            IndexDesignFindingKind.MergeableIndexesDifferingIncludeOnly,
            table.QualifiedName,
            b.Name,
            $"'{table.QualifiedName}' indexes '{a.Name ?? UnnamedIndexPlaceholder}' and '{b.Name ?? UnnamedIndexPlaceholder}' share the identical key list ({string.Join(", ", a.KeyColumns)}) and sort direction but carry different, non-overlapping INCLUDE columns ('{a.Name ?? UnnamedIndexPlaceholder}': {string.Join(", ", a.IncludedColumns)}; '{b.Name ?? UnnamedIndexPlaceholder}': {string.Join(", ", b.IncludedColumns)}) - mergeable into one index carrying the union ({union}) at no seek cost to either original query, for less write/storage overhead than carrying both.",
            table.SourcePath,
            table.SourceLine));
    }

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
            $"'{table.QualifiedName}' carries a columnstore index ('{columnstoreIndex.Name ?? UnnamedIndexPlaceholder}') and is also a direct INSERT/UPDATE/DELETE/MERGE target elsewhere in this codebase - lock escalation on a columnstore index happens at ROWGROUP granularity, not row granularity, so a single-row write inside an explicit transaction can block unrelated concurrent access to every other row sharing that rowgroup. Structural risk flag only: whether contention actually occurs is workload-dependent (concurrent access pattern, rowgroup size) and out of reach for this static pass.",
            table.SourcePath,
            table.SourceLine,
            FindingConfidence.Medium));
    }

    private static void ScanMonotonicClusteredKeyMissingSequentialOptimization(CatalogTable table, StringComparer identifierComparer, List<IndexDesignFinding> findings)
    {
        var clusteredIndex = table.Indexes.FirstOrDefault(i => i.IsClustered && !i.IsColumnstore && !i.IsDisabled);
        if (clusteredIndex is null || clusteredIndex.KeyColumns.Count == 0 || clusteredIndex.OptimizeForSequentialKey)
        {
            return;
        }

        var leadingColumn = table.FindColumn(clusteredIndex.KeyColumns[0], identifierComparer);
        if (leadingColumn is not { IsIdentity: true, IdentityIncrement: > 0 })
        {
            return;
        }

        findings.Add(new IndexDesignFinding(
            IndexDesignFindingKind.MonotonicClusteredKeyMissingSequentialOptimization,
            table.QualifiedName,
            clusteredIndex.Name,
            $"'{table.QualifiedName}' clustered index '{clusteredIndex.Name ?? UnnamedIndexPlaceholder}' leads on '{leadingColumn.Name}', an always-ascending IDENTITY column, with OPTIMIZE_FOR_SEQUENTIAL_KEY not enabled - every insert lands on the same trailing page, so concurrent inserts can serialize on that page's latch. Structural risk flag only: whether this actually causes contention depends on concurrent insert rate, which is workload data out of reach for this static pass.",
            table.SourcePath,
            table.SourceLine,
            FindingConfidence.Medium));
    }

    private static void ScanNonAlignedPartitionedIndex(CatalogTable table, StringComparer identifierComparer, List<IndexDesignFinding> findings)
    {
        var clusteredIndex = table.Indexes.FirstOrDefault(i => i.IsClustered && !i.IsColumnstore);
        if (clusteredIndex?.PartitionSchemeName is not { } tablePartitionScheme)
        {

            return;
        }

        foreach (var index in table.Indexes)
        {
            if (ReferenceEquals(index, clusteredIndex) || index.IsDisabled || index.IsColumnstore)
            {
                continue;
            }

            var isAligned = identifierComparer.Equals(index.PartitionSchemeName, tablePartitionScheme)
                && identifierComparer.Equals(index.PartitioningColumnName, clusteredIndex.PartitioningColumnName);
            if (isAligned)
            {
                continue;
            }

            var whereText = index.PartitionSchemeName is { } indexScheme
                ? $"is itself partitioned on scheme '{indexScheme}' keyed on '{index.PartitioningColumnName ?? "<unresolved>"}'"
                : "sits on a single, unpartitioned filegroup";
            findings.Add(new IndexDesignFinding(
                IndexDesignFindingKind.NonAlignedPartitionedIndex,
                table.QualifiedName,
                index.Name,
                $"'{table.QualifiedName}' is partitioned on scheme '{tablePartitionScheme}' keyed on '{clusteredIndex.PartitioningColumnName ?? "<unresolved>"}', but index '{index.Name ?? UnnamedIndexPlaceholder}' {whereText} - not aligned with the table. A non-aligned index cannot participate in a partition SWITCH against this table, and per-partition maintenance on it degrades to a full-index operation.",
                table.SourcePath,
                table.SourceLine));
        }
    }

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
        $"{tableQualifiedName}\x01{columnName}";
}
