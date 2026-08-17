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
}
