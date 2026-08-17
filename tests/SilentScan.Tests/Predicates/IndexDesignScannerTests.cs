using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Catalog-only pass (docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §A
/// "Physical/schema design", the clustered/nonclustered-flag-dependent group). <see
/// cref="CatalogIndex.IsClustered"/> is only ever populated live (<see
/// cref="SilentScan.Verify.Catalog.LiveCatalogReader"/>) - these tests build the catalog directly,
/// the same shape <c>CrossTableTypeDriftScannerTests</c> already established for a live-only-input
/// scanner, to exercise the scanner's own logic without needing the Docker oracle for every case.
/// </summary>
public sealed class IndexDesignScannerTests
{
    private static CatalogTable Table(
        string schema, string name, IReadOnlyList<CatalogColumn> columns, IReadOnlyList<CatalogIndex> indexes,
        bool isMemoryOptimized = false) =>
        new(schema, name, CatalogTableKind.Table, columns, indexes,
            SourcePath: $"{schema}.{name}", SourceLine: 1, IsMemoryOptimized: isMemoryOptimized);

    private static CatalogColumn Column(string name, SqlType? type, bool isNullable = false) =>
        new(name, type, isNullable, IsIdentity: false, IsComputed: false, IsPersisted: false);

    private static readonly SqlType IntType = new(SqlTypeCategory.Int);
    private static readonly SqlType GuidType = new(SqlTypeCategory.UniqueIdentifier);

    [Fact]
    public void HeapWithNonclusteredIndex_Fires()
    {
        var catalog = new DatabaseCatalog();
        var index = new CatalogIndex("IX_Orders_CustomerId", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], [], IsClustered: false);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType), Column("CustomerId", IntType)], [index]));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings);
        Assert.Equal(IndexDesignFindingKind.HeapWithNonclusteredIndexes, finding.Kind);
        Assert.Equal("dbo.Orders", finding.TableQualifiedName);
    }

    [Fact]
    public void HeapWithZeroIndexes_NeverFires()
    {
        // Deliberately excluded - a heap with no indexes at all is a common, often deliberate
        // staging-table design, not this finding's target.
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "StagingImport", [Column("Id", IntType)], []));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void TableWithClusteredIndex_NeverFiresHeapFindings()
    {
        var catalog = new DatabaseCatalog();
        var clustered = new CatalogIndex("PK_Orders", CatalogIndexKind.PrimaryKey, IsUnique: true, ["Id"], [], IsClustered: true);
        var nonclustered = new CatalogIndex("IX_Orders_CustomerId", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], [], IsClustered: false);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType), Column("CustomerId", IntType)], [clustered, nonclustered]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void HeapWithNonclusteredPrimaryKey_FiresSharperKindOnly()
    {
        var catalog = new DatabaseCatalog();
        var nonclusteredPk = new CatalogIndex("PK_Orders", CatalogIndexKind.PrimaryKey, IsUnique: true, ["Id"], [], IsClustered: false);
        var secondaryIndex = new CatalogIndex("IX_Orders_CustomerId", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], [], IsClustered: false);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType), Column("CustomerId", IntType)], [nonclusteredPk, secondaryIndex]));

        var findings = IndexDesignScanner.Scan(catalog);

        // Only the sharper kind fires - the general "heap with nonclustered indexes" finding is
        // subsumed, never reported twice for the same underlying cause.
        var finding = Assert.Single(findings);
        Assert.Equal(IndexDesignFindingKind.HeapWithNonclusteredPrimaryKey, finding.Kind);
    }

    [Fact]
    public void HeapOnMemoryOptimizedTable_NeverFires()
    {
        // A memory-optimized table has no on-disk heap/RID storage at all and never carries a
        // type=1 CLUSTERED row - naive heap detection would otherwise misfire on every one.
        var catalog = new DatabaseCatalog();
        var hashIndex = new CatalogIndex("IX_Orders_CustomerId", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], [], IsClustered: false);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType), Column("CustomerId", IntType)], [hashIndex], isMemoryOptimized: true));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void ClusteredColumnstoreTable_NeverReadAsHeap()
    {
        // A clustered columnstore index has no sys.index_columns rows of its own (no traditional
        // key), so it carries no KeyColumns - but it still means the table is NOT a heap.
        var catalog = new DatabaseCatalog();
        var cci = new CatalogIndex(null, CatalogIndexKind.Index, IsUnique: false, [], [], IsColumnstore: true, IsClustered: true);
        var secondaryIndex = new CatalogIndex("IX_Orders_CustomerId", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], [], IsClustered: false);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType), Column("CustomerId", IntType)], [cci, secondaryIndex]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void NonUniqueClusteredIndex_Fires()
    {
        var catalog = new DatabaseCatalog();
        var clustered = new CatalogIndex("CIX_Orders", CatalogIndexKind.Index, IsUnique: false, ["CreatedDate"], [], IsClustered: true);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("CreatedDate", new SqlType(SqlTypeCategory.DateTime))], [clustered]));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.Kind == IndexDesignFindingKind.NonUniqueClusteredIndex);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void UniqueClusteredIndex_NeverFiresNonUniqueKind()
    {
        var catalog = new DatabaseCatalog();
        var clustered = new CatalogIndex("PK_Orders", CatalogIndexKind.PrimaryKey, IsUnique: true, ["Id"], [], IsClustered: true);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType)], [clustered]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.NonUniqueClusteredIndex);
    }

    [Fact]
    public void ClusteredColumnstore_NeverFiresNonUniqueOrWideOrGuidKinds()
    {
        // A CCI has no traditional key/uniquifier concept - IsClustered alone must never be used
        // to drive the clustering-key-quality checks, only IsClustered && !IsColumnstore.
        var catalog = new DatabaseCatalog();
        var cci = new CatalogIndex(null, CatalogIndexKind.Index, IsUnique: false, [], [], IsColumnstore: true, IsClustered: true);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType)], [cci]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void WideClusteredKey_FiresOnColumnCount()
    {
        var catalog = new DatabaseCatalog();
        var columns = new[] { "A", "B", "C", "D" };
        var clustered = new CatalogIndex("CIX_Wide", CatalogIndexKind.Index, IsUnique: true, columns, [], IsClustered: true);
        catalog.AddOrReplace(Table("dbo", "Wide", [.. columns.Select(c => Column(c, IntType))], [clustered]));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.Kind == IndexDesignFindingKind.WideClusteredKey);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void WideClusteredKey_FiresOnByteWidth()
    {
        var catalog = new DatabaseCatalog();
        // A single nvarchar(20) column is 40 bytes - over the 16-byte threshold on its own,
        // despite being only one key column (so the column-count half never fires here).
        var wideString = new SqlType(SqlTypeCategory.NVarChar, Length: 20);
        var clustered = new CatalogIndex("CIX_Wide", CatalogIndexKind.Index, IsUnique: true, ["Code"], [], IsClustered: true);
        catalog.AddOrReplace(Table("dbo", "Wide", [Column("Code", wideString)], [clustered]));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.Kind == IndexDesignFindingKind.WideClusteredKey);
        Assert.Contains("40 bytes", finding.DetailText);
    }

    [Fact]
    public void NarrowClusteredKey_NeverFiresWideKind()
    {
        var catalog = new DatabaseCatalog();
        var clustered = new CatalogIndex("PK_Orders", CatalogIndexKind.PrimaryKey, IsUnique: true, ["Id"], [], IsClustered: true);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType)], [clustered]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.WideClusteredKey);
    }

    [Fact]
    public void WideClusteredKey_UnresolvedColumnType_NeverGuessesByteWidth()
    {
        // A column whose type never resolved (e.g. a CLR UDT/geography this catalog can't map)
        // must drop the byte-based half of the check entirely rather than report a lower-bound
        // total as if it were the real key width.
        var catalog = new DatabaseCatalog();
        var clustered = new CatalogIndex("CIX_Test", CatalogIndexKind.Index, IsUnique: true, ["A", "B"], [], IsClustered: true);
        catalog.AddOrReplace(Table("dbo", "T", [Column("A", IntType), Column("B", type: null)], [clustered]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.WideClusteredKey);
    }

    [Fact]
    public void GuidClusteredKeyWithNewIdDefault_Fires()
    {
        var catalog = new DatabaseCatalog();
        var clustered = new CatalogIndex("PK_Orders", CatalogIndexKind.PrimaryKey, IsUnique: true, ["Id"], [], IsClustered: true);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", GuidType)], [clustered]));
        catalog.AddSchemaExpression(new SchemaExpressionReference(
            SchemaDependencyKind.DefaultConstraint, "dbo.Orders", "Id", "(newid())", "dbo.Orders", 1));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.Kind == IndexDesignFindingKind.RandomClusteredKeyGuidDefault);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void GuidClusteredKeyWithNewSequentialIdDefault_NeverFires()
    {
        // The precision guard the checklist explicitly calls out: NEWSEQUENTIALID() must NOT
        // trip the same finding as NEWID() - it avoids the random-insert problem this kind
        // targets.
        var catalog = new DatabaseCatalog();
        var clustered = new CatalogIndex("PK_Orders", CatalogIndexKind.PrimaryKey, IsUnique: true, ["Id"], [], IsClustered: true);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", GuidType)], [clustered]));
        catalog.AddSchemaExpression(new SchemaExpressionReference(
            SchemaDependencyKind.DefaultConstraint, "dbo.Orders", "Id", "(newsequentialid())", "dbo.Orders", 1));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.RandomClusteredKeyGuidDefault);
    }

    [Fact]
    public void GuidClusteredKeyWithNoDefault_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        var clustered = new CatalogIndex("PK_Orders", CatalogIndexKind.PrimaryKey, IsUnique: true, ["Id"], [], IsClustered: true);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", GuidType)], [clustered]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.RandomClusteredKeyGuidDefault);
    }

    [Fact]
    public void NonGuidClusteredKeyWithNewIdDefault_NeverFires()
    {
        // NEWID() on a non-uniqueidentifier column (impossible in real T-SQL, but the scanner's
        // own type guard should never fire regardless) - belt-and-suspenders for the type check.
        var catalog = new DatabaseCatalog();
        var clustered = new CatalogIndex("PK_Orders", CatalogIndexKind.PrimaryKey, IsUnique: true, ["Id"], [], IsClustered: true);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType)], [clustered]));
        catalog.AddSchemaExpression(new SchemaExpressionReference(
            SchemaDependencyKind.DefaultConstraint, "dbo.Orders", "Id", "(newid())", "dbo.Orders", 1));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.RandomClusteredKeyGuidDefault);
    }

    [Theory]
    [InlineData("(newid())")]
    [InlineData("newid()")]
    [InlineData(" ( NEWID ( ) ) ")]
    public void NewIdDefaultTextVariants_AllRecognized(string definitionText)
    {
        var catalog = new DatabaseCatalog();
        var clustered = new CatalogIndex("PK_Orders", CatalogIndexKind.PrimaryKey, IsUnique: true, ["Id"], [], IsClustered: true);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", GuidType)], [clustered]));
        catalog.AddSchemaExpression(new SchemaExpressionReference(
            SchemaDependencyKind.DefaultConstraint, "dbo.Orders", "Id", definitionText, "dbo.Orders", 1));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.Contains(findings, f => f.Kind == IndexDesignFindingKind.RandomClusteredKeyGuidDefault);
    }

    [Fact]
    public void ViewsAndTempTables_NeverScanned()
    {
        // This pass is scoped to real base tables only (CatalogTableKind.Table) - a view/temp
        // table/table variable has no physical heap/clustered storage of its own to reason about
        // the same way.
        var catalog = new DatabaseCatalog();
        var nonclustered = new CatalogIndex("IX", CatalogIndexKind.Index, IsUnique: false, ["A"], [], IsClustered: false);
        catalog.AddOrReplace(new CatalogTable("dbo", "SomeView", CatalogTableKind.TemporaryTable,
            [Column("A", IntType)], [nonclustered], SourcePath: "dbo.SomeView", SourceLine: 1));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.Empty(findings);
    }

    // docs/detection-checklist.md §A "Duplicate and prefix-subsumed indexes".

    [Fact]
    public void ExactDuplicateIndexes_Fire()
    {
        var catalog = new DatabaseCatalog();
        var a = new CatalogIndex("IX_A", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], []);
        var b = new CatalogIndex("IX_B", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], []);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType), Column("CustomerId", IntType)], [a, b]));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.Kind == IndexDesignFindingKind.DuplicateIndex);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void DifferentUniquenessOrKind_NeverFiresDuplicate()
    {
        // The checklist's own precision guard: uniqueness and index kind must both match too.
        var catalog = new DatabaseCatalog();
        var unique = new CatalogIndex("UQ_A", CatalogIndexKind.UniqueConstraint, IsUnique: true, ["CustomerId"], []);
        var nonUnique = new CatalogIndex("IX_B", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], []);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType), Column("CustomerId", IntType)], [unique, nonUnique]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind is IndexDesignFindingKind.DuplicateIndex or IndexDesignFindingKind.SubsumedIndex);
    }

    [Fact]
    public void FilteredIndexes_NeverComparedForDuplicateOrSubsumed()
    {
        // Filter predicate TEXT isn't read by this catalog - two filtered indexes' definitions
        // can never be confirmed equal, so they're excluded from comparison entirely.
        var catalog = new DatabaseCatalog();
        var a = new CatalogIndex("IX_A", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], [], IsFiltered: true);
        var b = new CatalogIndex("IX_B", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], [], IsFiltered: true);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType), Column("CustomerId", IntType)], [a, b]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind is IndexDesignFindingKind.DuplicateIndex or IndexDesignFindingKind.SubsumedIndex);
    }

    [Fact]
    public void PrefixSubsumedIndex_Fires()
    {
        var catalog = new DatabaseCatalog();
        var narrow = new CatalogIndex("IX_Narrow", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], []);
        var wide = new CatalogIndex("IX_Wide", CatalogIndexKind.Index, IsUnique: false, ["CustomerId", "OrderDate"], []);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType), Column("CustomerId", IntType), Column("OrderDate", IntType)], [narrow, wide]));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.Kind == IndexDesignFindingKind.SubsumedIndex);
        Assert.Equal("IX_Narrow", finding.IndexName);
    }

    [Fact]
    public void SameLengthDifferentColumns_NeverFiresSubsumed()
    {
        var catalog = new DatabaseCatalog();
        var a = new CatalogIndex("IX_A", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], []);
        var b = new CatalogIndex("IX_B", CatalogIndexKind.Index, IsUnique: false, ["OrderDate"], []);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType), Column("CustomerId", IntType), Column("OrderDate", IntType)], [a, b]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind is IndexDesignFindingKind.DuplicateIndex or IndexDesignFindingKind.SubsumedIndex);
    }

    [Fact]
    public void NonPrefixColumnOrder_NeverFiresSubsumed()
    {
        // (OrderDate, CustomerId) is NOT a prefix match for (CustomerId, OrderDate) - order matters.
        var catalog = new DatabaseCatalog();
        var a = new CatalogIndex("IX_A", CatalogIndexKind.Index, IsUnique: false, ["OrderDate"], []);
        var b = new CatalogIndex("IX_B", CatalogIndexKind.Index, IsUnique: false, ["CustomerId", "OrderDate"], []);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType), Column("CustomerId", IntType), Column("OrderDate", IntType)], [a, b]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.SubsumedIndex);
    }

    [Fact]
    public void SubsumedIndexWithUncoveredInclude_NeverFires()
    {
        // The narrower index's own INCLUDE column ("Note") is NOT covered by the wider index -
        // the wider index cannot serve every seek the narrower one could, so this must not fire.
        var catalog = new DatabaseCatalog();
        var narrow = new CatalogIndex("IX_Narrow", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], ["Note"]);
        var wide = new CatalogIndex("IX_Wide", CatalogIndexKind.Index, IsUnique: false, ["CustomerId", "OrderDate"], []);
        catalog.AddOrReplace(Table("dbo", "Orders",
            [Column("Id", IntType), Column("CustomerId", IntType), Column("OrderDate", IntType), Column("Note", IntType)],
            [narrow, wide]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.SubsumedIndex);
    }

    [Fact]
    public void DisabledIndex_ExcludedFromDuplicateComparison()
    {
        var catalog = new DatabaseCatalog();
        var active = new CatalogIndex("IX_Active", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], []);
        var disabled = new CatalogIndex("IX_Disabled", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], [], IsDisabled: true);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType), Column("CustomerId", IntType)], [active, disabled]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind is IndexDesignFindingKind.DuplicateIndex or IndexDesignFindingKind.SubsumedIndex);
    }

    // docs/detection-checklist.md §A "Disabled and hypothetical indexes".

    [Fact]
    public void DisabledIndex_FiresOwnKind()
    {
        var catalog = new DatabaseCatalog();
        var disabled = new CatalogIndex("IX_Old", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], [], IsDisabled: true);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType), Column("CustomerId", IntType)], [disabled]));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.Kind == IndexDesignFindingKind.DisabledIndex);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void HypotheticalIndex_FiresHypotheticalKindOnly_NeverDisabledToo()
    {
        // Microsoft's own documentation: a hypothetical index always carries is_disabled = 1 too -
        // must never double-report the same row under both kinds.
        var catalog = new DatabaseCatalog();
        var hypothetical = new CatalogIndex(
            "_dta_index_Orders_5_1234", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], [],
            IsDisabled: true, IsHypothetical: true);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType), Column("CustomerId", IntType)], [hypothetical]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.Single(findings);
        Assert.Contains(findings, f => f.Kind == IndexDesignFindingKind.HypotheticalIndex);
        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.DisabledIndex);
    }

    [Fact]
    public void EnabledIndex_NeverFiresDisabledOrHypothetical()
    {
        var catalog = new DatabaseCatalog();
        var active = new CatalogIndex("IX_Active", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], []);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType), Column("CustomerId", IntType)], [active]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind is IndexDesignFindingKind.DisabledIndex or IndexDesignFindingKind.HypotheticalIndex);
    }

    // docs/detection-checklist.md §A "Over-indexing".

    [Fact]
    public void ManyNonclusteredIndexes_Fires()
    {
        var catalog = new DatabaseCatalog();
        var indexes = Enumerable.Range(0, IndexDesignScanner.ManyNonclusteredIndexesThreshold)
            .Select(i => new CatalogIndex($"IX_{i}", CatalogIndexKind.Index, IsUnique: false, [$"Col{i}"], []))
            .ToList();
        catalog.AddOrReplace(Table("dbo", "Orders",
            [.. indexes.Select(ix => Column(ix.KeyColumns[0], IntType))], indexes));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.Kind == IndexDesignFindingKind.ManyNonclusteredIndexes);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
        Assert.Null(finding.IndexName);
        Assert.DoesNotContain("drop this", finding.DetailText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FewNonclusteredIndexes_NeverFiresManyKind()
    {
        var catalog = new DatabaseCatalog();
        var indexes = Enumerable.Range(0, IndexDesignScanner.ManyNonclusteredIndexesThreshold - 1)
            .Select(i => new CatalogIndex($"IX_{i}", CatalogIndexKind.Index, IsUnique: false, [$"Col{i}"], []))
            .ToList();
        catalog.AddOrReplace(Table("dbo", "Orders",
            [.. indexes.Select(ix => Column(ix.KeyColumns[0], IntType))], indexes));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.ManyNonclusteredIndexes);
    }

    [Fact]
    public void ManyKeyColumnsIndex_Fires()
    {
        var catalog = new DatabaseCatalog();
        var columns = Enumerable.Range(0, IndexDesignScanner.ManyKeyColumnsThreshold).Select(i => $"Col{i}").ToArray();
        var wideIndex = new CatalogIndex("IX_Wide", CatalogIndexKind.Index, IsUnique: false, columns, []);
        catalog.AddOrReplace(Table("dbo", "Orders", [.. columns.Select(c => Column(c, IntType))], [wideIndex]));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.Kind == IndexDesignFindingKind.ManyKeyColumnsIndex);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void WideClusteredKey_NeverAlsoFiresManyKeyColumnsIndex()
    {
        // Never double-reported: WideClusteredKey already covers the clustered index at its own,
        // tighter threshold.
        var catalog = new DatabaseCatalog();
        var columns = Enumerable.Range(0, IndexDesignScanner.ManyKeyColumnsThreshold).Select(i => $"Col{i}").ToArray();
        var clustered = new CatalogIndex("CIX_Wide", CatalogIndexKind.Index, IsUnique: true, columns, [], IsClustered: true);
        catalog.AddOrReplace(Table("dbo", "Orders", [.. columns.Select(c => Column(c, IntType))], [clustered]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.ManyKeyColumnsIndex);
        Assert.Contains(findings, f => f.Kind == IndexDesignFindingKind.WideClusteredKey);
    }

    // docs/detection-checklist.md §A "Unindexed foreign key columns".

    [Fact]
    public void UnindexedForeignKey_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType), Column("CustomerId", IntType)], []));
        catalog.AddOrReplace(Table("dbo", "Customers", [Column("Id", IntType)], []));
        catalog.AddForeignKey(new ForeignKeyRelationship(
            "FK_Orders_Customers", "dbo.Orders", "CustomerId", "dbo.Customers", "Id"));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.Kind == IndexDesignFindingKind.UnindexedForeignKey);
        Assert.Equal("dbo.Orders", finding.TableQualifiedName);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void ForeignKeyWithLeadingIndex_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        var leadingIndex = new CatalogIndex("IX_Orders_CustomerId", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], []);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType), Column("CustomerId", IntType)], [leadingIndex]));
        catalog.AddOrReplace(Table("dbo", "Customers", [Column("Id", IntType)], []));
        catalog.AddForeignKey(new ForeignKeyRelationship(
            "FK_Orders_Customers", "dbo.Orders", "CustomerId", "dbo.Customers", "Id"));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.UnindexedForeignKey);
    }

    [Fact]
    public void CompositeForeignKey_LeadingCompositeIndex_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        var compositeIndex = new CatalogIndex("IX_Orders_Composite", CatalogIndexKind.Index, IsUnique: false, ["CustomerId", "RegionId"], []);
        catalog.AddOrReplace(Table("dbo", "Orders",
            [Column("Id", IntType), Column("CustomerId", IntType), Column("RegionId", IntType)], [compositeIndex]));
        catalog.AddOrReplace(Table("dbo", "Customers", [Column("Id", IntType), Column("RegionId", IntType)], []));
        catalog.AddForeignKey(new ForeignKeyRelationship(
            "FK_Orders_Customers", "dbo.Orders", "CustomerId", "dbo.Customers", "Id"));
        catalog.AddForeignKey(new ForeignKeyRelationship(
            "FK_Orders_Customers", "dbo.Orders", "RegionId", "dbo.Customers", "RegionId"));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.UnindexedForeignKey);
    }

    [Fact]
    public void ForeignKeyCoveredOnlyByFilteredIndex_StillFires()
    {
        // A filtered index only covers rows matching its predicate - it can never guarantee
        // coverage for an FK's own RI-check/join usage the way an unfiltered index can.
        var catalog = new DatabaseCatalog();
        var filteredIndex = new CatalogIndex("IX_Filtered", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], [], IsFiltered: true);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType), Column("CustomerId", IntType)], [filteredIndex]));
        catalog.AddOrReplace(Table("dbo", "Customers", [Column("Id", IntType)], []));
        catalog.AddForeignKey(new ForeignKeyRelationship(
            "FK_Orders_Customers", "dbo.Orders", "CustomerId", "dbo.Customers", "Id"));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.Contains(findings, f => f.Kind == IndexDesignFindingKind.UnindexedForeignKey);
    }

    // docs/detection-checklist.md §A, the three "lower-precision, listed for completeness" table-shape signals.

    [Fact]
    public void WideTableByColumnCount_Fires()
    {
        var catalog = new DatabaseCatalog();
        var columns = Enumerable.Range(0, IndexDesignScanner.WideTableMinColumns)
            .Select(i => Column($"Col{i}", IntType)).ToList();
        catalog.AddOrReplace(Table("dbo", "Wide", columns, []));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.Kind == IndexDesignFindingKind.WideTable);
        Assert.Equal(FindingConfidence.Low, finding.Confidence);
    }

    [Fact]
    public void NarrowTable_NeverFiresWideTable()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Narrow", [Column("Id", IntType), Column("Name", IntType)], []));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.WideTable);
    }

    [Fact]
    public void HighNullableColumnRatio_Fires()
    {
        var catalog = new DatabaseCatalog();
        var columns = new List<CatalogColumn>
        {
            Column("Id", IntType, isNullable: false),
            Column("A", IntType, isNullable: true),
            Column("B", IntType, isNullable: true),
            Column("C", IntType, isNullable: true),
            Column("D", IntType, isNullable: true),
        };
        catalog.AddOrReplace(Table("dbo", "MostlyNull", columns, []));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.Kind == IndexDesignFindingKind.HighNullableColumnRatio);
        Assert.Equal(FindingConfidence.Low, finding.Confidence);
    }

    [Fact]
    public void TooFewColumns_NeverFiresRatioChecks_EvenAt100Percent()
    {
        // Below RatioChecksMinColumns - a trivial 2-column table hitting 100% on a ratio means
        // nothing.
        var catalog = new DatabaseCatalog();
        var columns = new List<CatalogColumn>
        {
            Column("A", IntType, isNullable: true),
            Column("B", IntType, isNullable: true),
        };
        catalog.AddOrReplace(Table("dbo", "TinyNullable", columns, []));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.HighNullableColumnRatio);
    }

    [Fact]
    public void HighStringColumnRatio_Fires()
    {
        var catalog = new DatabaseCatalog();
        var stringType = new SqlType(SqlTypeCategory.VarChar, Length: 50);
        var columns = new List<CatalogColumn>
        {
            Column("Id", IntType),
            Column("A", stringType),
            Column("B", stringType),
            Column("C", stringType),
            Column("D", stringType),
        };
        catalog.AddOrReplace(Table("dbo", "MostlyString", columns, []));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.Kind == IndexDesignFindingKind.HighStringColumnRatio);
        Assert.Equal(FindingConfidence.Low, finding.Confidence);
    }

    [Fact]
    public void LowStringColumnRatio_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        var stringType = new SqlType(SqlTypeCategory.VarChar, Length: 50);
        var columns = new List<CatalogColumn>
        {
            Column("Id", IntType),
            Column("A", IntType),
            Column("B", IntType),
            Column("C", IntType),
            Column("D", stringType),
        };
        catalog.AddOrReplace(Table("dbo", "MostlyNumeric", columns, []));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.HighStringColumnRatio);
    }

    [Fact]
    public void FilteredIndex_FilterColumnMissingFromKeyAndInclude_Fires()
    {
        var catalog = new DatabaseCatalog();
        // Filters on IsActive, but only carries CustomerId as its key - IsActive is neither a key
        // column nor an INCLUDE column, so the engine can't confirm the filter still holds without
        // re-reading the base table.
        var filtered = new CatalogIndex(
            "IX_Orders_Active", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], [],
            IsFiltered: true, FilterDefinition: "([IsActive]=(1))");
        catalog.AddOrReplace(Table(
            "dbo", "Orders",
            [Column("CustomerId", IntType), Column("IsActive", new SqlType(SqlTypeCategory.Bit))],
            [filtered]));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.Kind == IndexDesignFindingKind.FilterColumnNotInIndex);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("IsActive", finding.DetailText);
    }

    [Fact]
    public void FilteredIndex_FilterColumnIsKeyColumn_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        var filtered = new CatalogIndex(
            "IX_Orders_Active", CatalogIndexKind.Index, IsUnique: false, ["IsActive"], [],
            IsFiltered: true, FilterDefinition: "([IsActive]=(1))");
        catalog.AddOrReplace(Table(
            "dbo", "Orders", [Column("IsActive", new SqlType(SqlTypeCategory.Bit))], [filtered]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.FilterColumnNotInIndex);
    }

    [Fact]
    public void FilteredIndex_FilterColumnIsIncludeColumn_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        var filtered = new CatalogIndex(
            "IX_Orders_Active", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], ["IsActive"],
            IsFiltered: true, FilterDefinition: "([IsActive]=(1))");
        catalog.AddOrReplace(Table(
            "dbo", "Orders",
            [Column("CustomerId", IntType), Column("IsActive", new SqlType(SqlTypeCategory.Bit))],
            [filtered]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.FilterColumnNotInIndex);
    }

    [Fact]
    public void FilteredIndex_UnparseableFilterText_NeverGuesses()
    {
        var catalog = new DatabaseCatalog();
        var filtered = new CatalogIndex(
            "IX_Orders_Weird", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], [],
            IsFiltered: true, FilterDefinition: "not valid t-sql at all (((");
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("CustomerId", IntType)], [filtered]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.FilterColumnNotInIndex);
    }

    [Theory]
    [InlineData(SqlTypeCategory.Text)]
    [InlineData(SqlTypeCategory.NText)]
    [InlineData(SqlTypeCategory.Image)]
    public void DeprecatedLobColumnType_Fires(SqlTypeCategory category)
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Legacy", [Column("Blob", new SqlType(category))], []));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.Kind == IndexDesignFindingKind.DeprecatedLobColumnType);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void TimestampColumn_FiresNamingKindOnly_LowConfidence()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Audited", [Column("RowVer", new SqlType(SqlTypeCategory.Timestamp))], []));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.Kind == IndexDesignFindingKind.TimestampColumnNaming);
        Assert.Equal(FindingConfidence.Low, finding.Confidence);
        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.DeprecatedLobColumnType);
    }

    [Fact]
    public void VarcharColumn_NeverFiresColumnTypeSignals()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Ordinary", [Column("Name", new SqlType(SqlTypeCategory.VarChar, Length: 50))], []));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind is IndexDesignFindingKind.DeprecatedLobColumnType or IndexDesignFindingKind.TimestampColumnNaming);
    }

    [Fact]
    public void FloatKeyColumn_Fires()
    {
        var catalog = new DatabaseCatalog();
        var index = new CatalogIndex("IX_Prices_Amount", CatalogIndexKind.Index, IsUnique: false, ["Amount"], []);
        catalog.AddOrReplace(Table("dbo", "Prices", [Column("Amount", new SqlType(SqlTypeCategory.Float))], [index]));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.Kind == IndexDesignFindingKind.FloatOrRealIndexKeyColumn);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void RealKeyColumn_Fires()
    {
        var catalog = new DatabaseCatalog();
        var index = new CatalogIndex("IX_Prices_Amount", CatalogIndexKind.Index, IsUnique: false, ["Amount"], []);
        catalog.AddOrReplace(Table("dbo", "Prices", [Column("Amount", new SqlType(SqlTypeCategory.Real))], [index]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.Single(findings, f => f.Kind == IndexDesignFindingKind.FloatOrRealIndexKeyColumn);
    }

    [Fact]
    public void FloatColumn_NotAKeyColumn_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        var index = new CatalogIndex("IX_Prices_CustomerId", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], ["Amount"]);
        catalog.AddOrReplace(Table(
            "dbo", "Prices",
            [Column("CustomerId", IntType), Column("Amount", new SqlType(SqlTypeCategory.Float))],
            [index]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.FloatOrRealIndexKeyColumn);
    }

    [Fact]
    public void FloatKeyColumn_OnDisabledIndex_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        var index = new CatalogIndex("IX_Prices_Amount", CatalogIndexKind.Index, IsUnique: false, ["Amount"], [], IsDisabled: true);
        catalog.AddOrReplace(Table("dbo", "Prices", [Column("Amount", new SqlType(SqlTypeCategory.Float))], [index]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.FloatOrRealIndexKeyColumn);
    }
}
