using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates;

public sealed class IndexDesignScannerTests
{
    private static CatalogTable Table(
        string schema, string name, IReadOnlyList<CatalogColumn> columns, IReadOnlyList<CatalogIndex> indexes,
        bool isMemoryOptimized = false, IReadOnlyList<CatalogStatisticsInfo>? statistics = null) =>
        new(schema, name, CatalogTableKind.Table, columns, indexes,
            SourcePath: $"{schema}.{name}", SourceLine: 1, IsMemoryOptimized: isMemoryOptimized, Statistics: statistics);

    private static CatalogColumn Column(string name, SqlType? type, bool isNullable = false) =>
        new(name, type, isNullable, IsIdentity: false, IsComputed: false, IsPersisted: false);

    private static readonly SqlType IntType = new(SqlTypeCategory.Int);
    private static readonly SqlType DateType = new(SqlTypeCategory.Date);
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

        var finding = Assert.Single(findings);
        Assert.Equal(IndexDesignFindingKind.HeapWithNonclusteredPrimaryKey, finding.Kind);
    }

    [Fact]
    public void HeapOnMemoryOptimizedTable_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        var hashIndex = new CatalogIndex("IX_Orders_CustomerId", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], [], IsClustered: false);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType), Column("CustomerId", IntType)], [hashIndex], isMemoryOptimized: true));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void ClusteredColumnstoreTable_NeverReadAsHeap()
    {
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
        var catalog = new DatabaseCatalog();
        var nonclustered = new CatalogIndex("IX", CatalogIndexKind.Index, IsUnique: false, ["A"], [], IsClustered: false);
        catalog.AddOrReplace(new CatalogTable("dbo", "SomeView", CatalogTableKind.TemporaryTable,
            [Column("A", IntType)], [nonclustered], SourcePath: "dbo.SomeView", SourceLine: 1));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.Empty(findings);
    }

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
        var catalog = new DatabaseCatalog();
        var columns = Enumerable.Range(0, IndexDesignScanner.ManyKeyColumnsThreshold).Select(i => $"Col{i}").ToArray();
        var clustered = new CatalogIndex("CIX_Wide", CatalogIndexKind.Index, IsUnique: true, columns, [], IsClustered: true);
        catalog.AddOrReplace(Table("dbo", "Orders", [.. columns.Select(c => Column(c, IntType))], [clustered]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.ManyKeyColumnsIndex);
        Assert.Contains(findings, f => f.Kind == IndexDesignFindingKind.WideClusteredKey);
    }

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
        var catalog = new DatabaseCatalog();
        var filteredIndex = new CatalogIndex("IX_Filtered", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], [], IsFiltered: true);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", IntType), Column("CustomerId", IntType)], [filteredIndex]));
        catalog.AddOrReplace(Table("dbo", "Customers", [Column("Id", IntType)], []));
        catalog.AddForeignKey(new ForeignKeyRelationship(
            "FK_Orders_Customers", "dbo.Orders", "CustomerId", "dbo.Customers", "Id"));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.Contains(findings, f => f.Kind == IndexDesignFindingKind.UnindexedForeignKey);
    }

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

    [Fact]
    public void StatisticsMarkedNoRecompute_Fires()
    {
        var catalog = new DatabaseCatalog();
        var stats = new CatalogStatisticsInfo("_WA_Sys_CustomerId", NoRecompute: true, IsAutoCreated: true);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("CustomerId", IntType)], [], statistics: [stats]));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings);
        Assert.Equal(IndexDesignFindingKind.NoRecomputeStatistics, finding.Kind);
        Assert.Equal("_WA_Sys_CustomerId", finding.IndexName);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void StatisticsWithAutomaticRecompute_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        var stats = new CatalogStatisticsInfo("_WA_Sys_CustomerId", NoRecompute: false, IsAutoCreated: true);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("CustomerId", IntType)], [], statistics: [stats]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.NoRecomputeStatistics);
    }

    [Fact]
    public void TableWithNoStatisticsInfo_NeverFiresNoRecompute()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("CustomerId", IntType)], []));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.NoRecomputeStatistics);
    }

    [Fact]
    public void NonclusteredKeyColumn_VarcharOverNonclusteredLimit_Fires()
    {
        var catalog = new DatabaseCatalog();
        var index = new CatalogIndex("IX_Wide", CatalogIndexKind.Index, IsUnique: false, ["Notes"], []);
        catalog.AddOrReplace(Table("dbo", "Docs", [Column("Notes", new SqlType(SqlTypeCategory.VarChar, Length: 1701))], [index]));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.Kind == IndexDesignFindingKind.VariableLengthKeyColumnExceedsKeyLimit);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void NonclusteredKeyColumn_VarcharAtNonclusteredLimit_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        var index = new CatalogIndex("IX_AtLimit", CatalogIndexKind.Index, IsUnique: false, ["Notes"], []);
        catalog.AddOrReplace(Table("dbo", "Docs", [Column("Notes", new SqlType(SqlTypeCategory.VarChar, Length: 1700))], [index]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.VariableLengthKeyColumnExceedsKeyLimit);
    }

    [Fact]
    public void ClusteredKeyColumn_VarcharOverClusteredButUnderNonclusteredLimit_Fires()
    {
        var catalog = new DatabaseCatalog();
        var index = new CatalogIndex("PK_Docs", CatalogIndexKind.PrimaryKey, IsUnique: true, ["Code"], [], IsClustered: true);
        catalog.AddOrReplace(Table("dbo", "Docs", [Column("Code", new SqlType(SqlTypeCategory.VarChar, Length: 901))], [index]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.Single(findings, f => f.Kind == IndexDesignFindingKind.VariableLengthKeyColumnExceedsKeyLimit);
    }

    [Fact]
    public void FixedLengthKeyColumn_CharOverLimit_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        var index = new CatalogIndex("PK_Docs", CatalogIndexKind.PrimaryKey, IsUnique: true, ["Code"], [], IsClustered: true);
        catalog.AddOrReplace(Table("dbo", "Docs", [Column("Code", new SqlType(SqlTypeCategory.Char, Length: 901))], [index]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.VariableLengthKeyColumnExceedsKeyLimit);
    }

    [Fact]
    public void TwoIndexes_SameKeySortDirection_NonOverlappingInclude_Fires()
    {
        var catalog = new DatabaseCatalog();
        var a = new CatalogIndex("IX_A", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], ["Email"], KeyColumnIsDescendingRaw: [false]);
        var b = new CatalogIndex("IX_B", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], ["Phone"], KeyColumnIsDescendingRaw: [false]);
        catalog.AddOrReplace(Table(
            "dbo", "Customers",
            [Column("CustomerId", IntType), Column("Email", new SqlType(SqlTypeCategory.VarChar, Length: 100)), Column("Phone", new SqlType(SqlTypeCategory.VarChar, Length: 20))],
            [a, b]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.Single(findings, f => f.Kind == IndexDesignFindingKind.MergeableIndexesDifferingIncludeOnly);
    }

    [Fact]
    public void TwoIndexes_SameKeyDifferentSortDirection_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        var a = new CatalogIndex("IX_A", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], ["Email"], KeyColumnIsDescendingRaw: [false]);
        var b = new CatalogIndex("IX_B", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], ["Phone"], KeyColumnIsDescendingRaw: [true]);
        catalog.AddOrReplace(Table(
            "dbo", "Customers",
            [Column("CustomerId", IntType), Column("Email", new SqlType(SqlTypeCategory.VarChar, Length: 100)), Column("Phone", new SqlType(SqlTypeCategory.VarChar, Length: 20))],
            [a, b]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.MergeableIndexesDifferingIncludeOnly);
    }

    [Fact]
    public void TwoIndexes_UnknownSortDirection_NeverGuessesMerge()
    {
        var catalog = new DatabaseCatalog();
        var a = new CatalogIndex("IX_A", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], ["Email"]);
        var b = new CatalogIndex("IX_B", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], ["Phone"]);
        catalog.AddOrReplace(Table(
            "dbo", "Customers",
            [Column("CustomerId", IntType), Column("Email", new SqlType(SqlTypeCategory.VarChar, Length: 100)), Column("Phone", new SqlType(SqlTypeCategory.VarChar, Length: 20))],
            [a, b]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.MergeableIndexesDifferingIncludeOnly);
    }

    [Fact]
    public void TwoIndexes_SubsetInclude_NeverFiresMergeable()
    {
        var catalog = new DatabaseCatalog();
        var a = new CatalogIndex("IX_A", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], ["Email"], KeyColumnIsDescendingRaw: [false]);
        var b = new CatalogIndex("IX_B", CatalogIndexKind.Index, IsUnique: false, ["CustomerId"], ["Email", "Phone"], KeyColumnIsDescendingRaw: [false]);
        catalog.AddOrReplace(Table(
            "dbo", "Customers",
            [Column("CustomerId", IntType), Column("Email", new SqlType(SqlTypeCategory.VarChar, Length: 100)), Column("Phone", new SqlType(SqlTypeCategory.VarChar, Length: 20))],
            [a, b]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.MergeableIndexesDifferingIncludeOnly);
    }

    [Fact]
    public void ColumnstoreIndex_OnDmlTargetTable_Fires()
    {
        var catalog = new DatabaseCatalog();
        var columnstore = new CatalogIndex("CCI_Facts", CatalogIndexKind.Index, IsUnique: false, [], [], IsClustered: true, IsColumnstore: true);
        catalog.AddOrReplace(Table("dbo", "Facts", [Column("Id", IntType)], [columnstore]));

        var findings = IndexDesignScanner.Scan(catalog, dmlTargetTables: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo.Facts" });

        var finding = Assert.Single(findings);
        Assert.Equal(IndexDesignFindingKind.ColumnstoreIndexOnDmlTargetTable, finding.Kind);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void ColumnstoreIndex_NotADmlTarget_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        var columnstore = new CatalogIndex("CCI_Facts", CatalogIndexKind.Index, IsUnique: false, [], [], IsClustered: true, IsColumnstore: true);
        catalog.AddOrReplace(Table("dbo", "Facts", [Column("Id", IntType)], [columnstore]));

        var findings = IndexDesignScanner.Scan(catalog, dmlTargetTables: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.ColumnstoreIndexOnDmlTargetTable);
    }

    [Fact]
    public void ColumnstoreIndex_NoDmlTargetSetProvided_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        var columnstore = new CatalogIndex("CCI_Facts", CatalogIndexKind.Index, IsUnique: false, [], [], IsClustered: true, IsColumnstore: true);
        catalog.AddOrReplace(Table("dbo", "Facts", [Column("Id", IntType)], [columnstore]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.ColumnstoreIndexOnDmlTargetTable);
    }

    [Fact]
    public void RowstoreIndex_OnDmlTargetTable_NeverFiresColumnstore()
    {
        var catalog = new DatabaseCatalog();
        var rowstore = new CatalogIndex("PK_Facts", CatalogIndexKind.PrimaryKey, IsUnique: true, ["Id"], [], IsClustered: true);
        catalog.AddOrReplace(Table("dbo", "Facts", [Column("Id", IntType)], [rowstore]));

        var findings = IndexDesignScanner.Scan(catalog, dmlTargetTables: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo.Facts" });

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.ColumnstoreIndexOnDmlTargetTable);
    }

    [Fact]
    public void IdentityClusteredKey_NoSequentialKeyOptimization_Fires()
    {
        var catalog = new DatabaseCatalog();
        var identityColumn = new CatalogColumn("Id", IntType, IsNullable: false, IsIdentity: true, IsComputed: false, IsPersisted: false, IdentitySeed: 1, IdentityIncrement: 1);
        var clustered = new CatalogIndex("PK_Orders", CatalogIndexKind.PrimaryKey, IsUnique: true, ["Id"], [], IsClustered: true, OptimizeForSequentialKey: false);
        catalog.AddOrReplace(Table("dbo", "Orders", [identityColumn], [clustered]));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings);
        Assert.Equal(IndexDesignFindingKind.MonotonicClusteredKeyMissingSequentialOptimization, finding.Kind);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void IdentityClusteredKey_SequentialKeyOptimizationAlreadyOn_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        var identityColumn = new CatalogColumn("Id", IntType, IsNullable: false, IsIdentity: true, IsComputed: false, IsPersisted: false, IdentitySeed: 1, IdentityIncrement: 1);
        var clustered = new CatalogIndex("PK_Orders", CatalogIndexKind.PrimaryKey, IsUnique: true, ["Id"], [], IsClustered: true, OptimizeForSequentialKey: true);
        catalog.AddOrReplace(Table("dbo", "Orders", [identityColumn], [clustered]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.MonotonicClusteredKeyMissingSequentialOptimization);
    }

    [Fact]
    public void NonIdentityClusteredKey_NeverFiresMonotonic()
    {
        var catalog = new DatabaseCatalog();
        var clustered = new CatalogIndex("PK_Orders", CatalogIndexKind.PrimaryKey, IsUnique: true, ["OrderCode"], [], IsClustered: true, OptimizeForSequentialKey: false);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20))], [clustered]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.MonotonicClusteredKeyMissingSequentialOptimization);
    }

    [Fact]
    public void IdentityClusteredKey_NegativeIncrement_NeverFiresMonotonic()
    {
        var catalog = new DatabaseCatalog();
        var identityColumn = new CatalogColumn("Id", IntType, IsNullable: false, IsIdentity: true, IsComputed: false, IsPersisted: false, IdentitySeed: 1000, IdentityIncrement: -1);
        var clustered = new CatalogIndex("PK_Orders", CatalogIndexKind.PrimaryKey, IsUnique: true, ["Id"], [], IsClustered: true, OptimizeForSequentialKey: false);
        catalog.AddOrReplace(Table("dbo", "Orders", [identityColumn], [clustered]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.MonotonicClusteredKeyMissingSequentialOptimization);
    }

    [Fact]
    public void NonclusteredIndexOnSingleFilegroup_WhileTablePartitioned_Fires()
    {
        var catalog = new DatabaseCatalog();
        var clustered = new CatalogIndex(
            "PK_Orders", CatalogIndexKind.PrimaryKey, IsUnique: true, ["OrderDate", "OrderId"], [],
            IsClustered: true, PartitionSchemeName: "PsOrderDate", PartitioningColumnName: "OrderDate");
        var nonAligned = new CatalogIndex(
            "IX_Orders_Region", CatalogIndexKind.Index, IsUnique: false, ["Region"], [],
            PartitionSchemeName: null, PartitioningColumnName: null);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("OrderDate", DateType), Column("OrderId", IntType), Column("Region", IntType)], [clustered, nonAligned]));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings);
        Assert.Equal(IndexDesignFindingKind.NonAlignedPartitionedIndex, finding.Kind);
        Assert.Equal("IX_Orders_Region", finding.IndexName);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void NonclusteredIndexOnSamePartitionScheme_KeyedOnDifferentColumn_Fires()
    {
        var catalog = new DatabaseCatalog();
        var clustered = new CatalogIndex(
            "PK_Orders", CatalogIndexKind.PrimaryKey, IsUnique: true, ["OrderDate", "OrderId"], [],
            IsClustered: true, PartitionSchemeName: "PsOrderDate", PartitioningColumnName: "OrderDate");
        var nonAligned = new CatalogIndex(
            "IX_Orders_Region", CatalogIndexKind.Index, IsUnique: false, ["Region"], [],
            PartitionSchemeName: "PsOrderDate", PartitioningColumnName: "Region");
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("OrderDate", DateType), Column("OrderId", IntType), Column("Region", IntType)], [clustered, nonAligned]));

        var findings = IndexDesignScanner.Scan(catalog);

        var finding = Assert.Single(findings);
        Assert.Equal(IndexDesignFindingKind.NonAlignedPartitionedIndex, finding.Kind);
    }

    [Fact]
    public void NonclusteredIndexAlignedOnSameSchemeAndColumn_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        var clustered = new CatalogIndex(
            "PK_Orders", CatalogIndexKind.PrimaryKey, IsUnique: true, ["OrderDate", "OrderId"], [],
            IsClustered: true, PartitionSchemeName: "PsOrderDate", PartitioningColumnName: "OrderDate");
        var aligned = new CatalogIndex(
            "IX_Orders_Region", CatalogIndexKind.Index, IsUnique: false, ["Region"], [],
            PartitionSchemeName: "PsOrderDate", PartitioningColumnName: "OrderDate");
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("OrderDate", DateType), Column("OrderId", IntType), Column("Region", IntType)], [clustered, aligned]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.NonAlignedPartitionedIndex);
    }

    [Fact]
    public void UnpartitionedTable_NeverFiresNonAligned()
    {
        var catalog = new DatabaseCatalog();
        var clustered = new CatalogIndex("PK_Orders", CatalogIndexKind.PrimaryKey, IsUnique: true, ["OrderId"], [], IsClustered: true);
        var nonclustered = new CatalogIndex("IX_Orders_Region", CatalogIndexKind.Index, IsUnique: false, ["Region"], []);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("OrderId", IntType), Column("Region", IntType)], [clustered, nonclustered]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.NonAlignedPartitionedIndex);
    }

    [Fact]
    public void PartitionedHeap_NeverFiresNonAligned()
    {
        var catalog = new DatabaseCatalog();
        var nonAligned = new CatalogIndex(
            "IX_Orders_Region", CatalogIndexKind.Index, IsUnique: false, ["Region"], [],
            PartitionSchemeName: null, PartitioningColumnName: null);
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("OrderId", IntType), Column("Region", IntType)], [nonAligned]));

        var findings = IndexDesignScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IndexDesignFindingKind.NonAlignedPartitionedIndex);
    }
}
