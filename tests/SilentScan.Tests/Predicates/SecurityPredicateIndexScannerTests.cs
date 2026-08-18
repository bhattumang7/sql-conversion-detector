using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md practitioner-sweep item "Row-Level Security predicate function with
/// no supporting index on its own filtered columns". <see cref="CatalogSecurityPredicate"/> is only
/// ever populated live - the hand-built-catalog tests below exercise the scanner's own reparse/
/// index-matching logic directly (the same "no Docker oracle needed to exercise the scanner's own
/// logic" shape <c>CheckConstraintScannerTests</c> already established), while the
/// <c>LiveDeployment_*</c> tests at the bottom prove the full live-read path against the real
/// standing Docker oracle (a fresh, disposable database, dropped unconditionally afterward) - the
/// scan-vs-seek mechanism and the "forces single-threaded execution" non-reproduction are both
/// documented with their own plan-XML evidence in <see cref="SecurityPredicateIndexFinding"/>'s own
/// doc comment.
/// </summary>
public sealed class SecurityPredicateIndexScannerTests
{
    private static CatalogTable Table(string schema, string name, IReadOnlyList<CatalogColumn> columns, IReadOnlyList<CatalogIndex> indexes) =>
        new(schema, name, CatalogTableKind.Table, columns, indexes, SourcePath: $"{schema}.{name}", SourceLine: 1);

    private static CatalogColumn Column(string name, bool isNullable = false) =>
        new(name, new SqlType(SqlTypeCategory.Int), isNullable, IsIdentity: false, IsComputed: false, IsPersisted: false);

    private static CatalogIndex Index(string name, params string[] keyColumns) =>
        new(name, CatalogIndexKind.Index, IsUnique: false, keyColumns, IncludedColumns: []);

    [Fact]
    public void EnabledFilterPredicate_BoundColumnHasNoIndex_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "T", [Column("Id"), Column("TenantId")], []));
        catalog.AddSecurityPredicate(new CatalogSecurityPredicate(
            "Security.TenantFilter", "dbo.T", "([Security].[fn_TenantPredicate]([TenantId]))",
            IsFilterPredicate: true, IsPolicyEnabled: true));

        var finding = Assert.Single(SecurityPredicateIndexScanner.Scan(catalog));

        Assert.Equal("dbo.T", finding.TableQualifiedName);
        Assert.Equal("Security.TenantFilter", finding.PolicyQualifiedName);
        Assert.Equal("Security.fn_TenantPredicate", finding.PredicateFunctionQualifiedName);
        Assert.Equal(["TenantId"], finding.FilteredColumns);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void EnabledFilterPredicate_BoundColumnLeadsAnActiveIndex_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table(
            "dbo", "T", [Column("Id"), Column("TenantId")],
            [Index("IX_T_TenantId", "TenantId")]));
        catalog.AddSecurityPredicate(new CatalogSecurityPredicate(
            "Security.TenantFilter", "dbo.T", "([Security].[fn_TenantPredicate]([TenantId]))",
            IsFilterPredicate: true, IsPolicyEnabled: true));

        Assert.Empty(SecurityPredicateIndexScanner.Scan(catalog));
    }

    [Fact]
    public void BoundColumnLeadsOnlyADisabledIndex_StillFires()
    {
        // A disabled index enforces/serves nothing today - the same "disabled doesn't count as
        // supporting" discipline IndexDesignScanner's own UnindexedForeignKey check already uses.
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table(
            "dbo", "T", [Column("Id"), Column("TenantId")],
            [new CatalogIndex("IX_T_TenantId", CatalogIndexKind.Index, IsUnique: false, ["TenantId"], [], IsDisabled: true)]));
        catalog.AddSecurityPredicate(new CatalogSecurityPredicate(
            "Security.TenantFilter", "dbo.T", "([Security].[fn_TenantPredicate]([TenantId]))",
            IsFilterPredicate: true, IsPolicyEnabled: true));

        Assert.Single(SecurityPredicateIndexScanner.Scan(catalog));
    }

    [Fact]
    public void BoundColumnIsNotTheLeadingKeyColumn_StillFires()
    {
        // TenantId is present in the index, but only as a non-leading column - an equality
        // seek against TenantId alone still cannot use this index, the same "leading column
        // only" discipline every other index-coverage check in this codebase uses.
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table(
            "dbo", "T", [Column("Id"), Column("TenantId"), Column("OtherKey")],
            [Index("IX_T_OtherKey_TenantId", "OtherKey", "TenantId")]));
        catalog.AddSecurityPredicate(new CatalogSecurityPredicate(
            "Security.TenantFilter", "dbo.T", "([Security].[fn_TenantPredicate]([TenantId]))",
            IsFilterPredicate: true, IsPolicyEnabled: true));

        Assert.Single(SecurityPredicateIndexScanner.Scan(catalog));
    }

    [Fact]
    public void DisabledPolicy_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "T", [Column("Id"), Column("TenantId")], []));
        catalog.AddSecurityPredicate(new CatalogSecurityPredicate(
            "Security.TenantFilter", "dbo.T", "([Security].[fn_TenantPredicate]([TenantId]))",
            IsFilterPredicate: true, IsPolicyEnabled: false));

        Assert.Empty(SecurityPredicateIndexScanner.Scan(catalog));
    }

    [Fact]
    public void BlockPredicate_NeverFires()
    {
        // A BLOCK predicate does not filter the table's own read path the way a FILTER predicate
        // does - see SecurityPredicateIndexFinding's own doc comment for why only FILTER is in scope.
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "T", [Column("Id"), Column("TenantId")], []));
        catalog.AddSecurityPredicate(new CatalogSecurityPredicate(
            "Security.TenantFilter", "dbo.T", "([Security].[fn_TenantPredicate]([TenantId]))",
            IsFilterPredicate: false, IsPolicyEnabled: true));

        Assert.Empty(SecurityPredicateIndexScanner.Scan(catalog));
    }

    [Fact]
    public void UnparseableDefinitionText_NeverGuesses()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "T", [Column("Id"), Column("TenantId")], []));
        catalog.AddSecurityPredicate(new CatalogSecurityPredicate(
            "Security.TenantFilter", "dbo.T", "not valid t-sql at all (((",
            IsFilterPredicate: true, IsPolicyEnabled: true));

        Assert.Empty(SecurityPredicateIndexScanner.Scan(catalog));
    }

    [Fact]
    public void EmptyDefinitionText_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "T", [Column("Id"), Column("TenantId")], []));
        catalog.AddSecurityPredicate(new CatalogSecurityPredicate(
            "Security.TenantFilter", "dbo.T", string.Empty,
            IsFilterPredicate: true, IsPolicyEnabled: true));

        Assert.Empty(SecurityPredicateIndexScanner.Scan(catalog));
    }

    [Fact]
    public void PredicateFunctionCalledWithNoColumnArgument_NeverGuesses()
    {
        // A literal argument (or no argument at all) binds to nothing this pass can check
        // against the table's own columns - never guessed at.
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "T", [Column("Id"), Column("TenantId")], []));
        catalog.AddSecurityPredicate(new CatalogSecurityPredicate(
            "Security.TenantFilter", "dbo.T", "([Security].[fn_AlwaysTrue]())",
            IsFilterPredicate: true, IsPolicyEnabled: true));

        Assert.Empty(SecurityPredicateIndexScanner.Scan(catalog));
    }

    [Fact]
    public void UnresolvableTargetTable_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddSecurityPredicate(new CatalogSecurityPredicate(
            "Security.TenantFilter", "dbo.GhostTable", "([Security].[fn_TenantPredicate]([TenantId]))",
            IsFilterPredicate: true, IsPolicyEnabled: true));

        Assert.Empty(SecurityPredicateIndexScanner.Scan(catalog));
    }

    [Fact]
    public void AtLeastOneBoundColumnIndexed_OtherUnindexedColumnStillDoesNotFireAlone()
    {
        // This pass cannot see the predicate function's own body, so it cannot tell whether
        // multiple bound columns combine with AND (a composite leading-prefix index would
        // suffice) or OR (each column needs its own index) - requiring every bound column to
        // individually lead some index is the direction that never over-reports either way.
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table(
            "dbo", "T", [Column("Id"), Column("TenantId"), Column("RegionId")],
            [Index("IX_T_TenantId", "TenantId")]));
        catalog.AddSecurityPredicate(new CatalogSecurityPredicate(
            "Security.TenantFilter", "dbo.T", "([Security].[fn_TenantRegionPredicate]([TenantId],[RegionId]))",
            IsFilterPredicate: true, IsPolicyEnabled: true));

        // TenantId leads an active index, so this deliberately conservative check never fires -
        // even though RegionId itself has no supporting index.
        Assert.Empty(SecurityPredicateIndexScanner.Scan(catalog));
    }

    /// <summary>
    /// End-to-end against the real standing Docker oracle (a fresh, disposable database, dropped
    /// unconditionally afterward) - proves the full live-read path (<c>LiveCatalogReader</c>'s new
    /// <see cref="DatabaseCatalog.SecurityPredicates"/> registry, through <see
    /// cref="SecurityPredicateIndexScanner"/>, into the real <see
    /// cref="SilentScan.Core.Reporting.ScanReport.SecurityPredicateIndexFindings"/> stream) works
    /// against a real <c>CREATE SECURITY POLICY</c>/<c>sys.security_predicates.predicate_definition</c>
    /// string, not just the hand-authored text used by the catalog-builder tests above.
    /// </summary>
    [Fact]
    public async Task LiveDeployment_UnindexedFilterPredicateColumn_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE SCHEMA RlsSec;
            GO
            CREATE TABLE dbo.RlsUnindexedTarget (Id INT NOT NULL PRIMARY KEY, TenantId INT NOT NULL, Payload NVARCHAR(50) NULL);
            GO
            CREATE FUNCTION RlsSec.fn_UnindexedTenantPredicate(@TenantId INT)
            RETURNS TABLE
            WITH SCHEMABINDING
            AS
            RETURN SELECT 1 AS fn_result WHERE @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS INT);
            GO
            CREATE SECURITY POLICY RlsSec.UnindexedTenantFilter
                ADD FILTER PREDICATE RlsSec.fn_UnindexedTenantPredicate(TenantId) ON dbo.RlsUnindexedTarget
                WITH (STATE = ON);
            GO
            """);

        var finding = Assert.Single(report.SecurityPredicateIndexFindings);
        Assert.Equal("dbo.RlsUnindexedTarget", finding.TableQualifiedName);
        Assert.Equal("RlsSec.UnindexedTenantFilter", finding.PolicyQualifiedName);
        Assert.Equal("RlsSec.fn_UnindexedTenantPredicate", finding.PredicateFunctionQualifiedName);
        Assert.Equal(["TenantId"], finding.FilteredColumns);
    }

    [Fact]
    public async Task LiveDeployment_IndexedFilterPredicateColumn_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE SCHEMA RlsSec;
            GO
            CREATE TABLE dbo.RlsIndexedTarget (Id INT NOT NULL PRIMARY KEY, TenantId INT NOT NULL, Payload NVARCHAR(50) NULL);
            CREATE INDEX IX_RlsIndexedTarget_TenantId ON dbo.RlsIndexedTarget(TenantId);
            GO
            CREATE FUNCTION RlsSec.fn_IndexedTenantPredicate(@TenantId INT)
            RETURNS TABLE
            WITH SCHEMABINDING
            AS
            RETURN SELECT 1 AS fn_result WHERE @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS INT);
            GO
            CREATE SECURITY POLICY RlsSec.IndexedTenantFilter
                ADD FILTER PREDICATE RlsSec.fn_IndexedTenantPredicate(TenantId) ON dbo.RlsIndexedTarget
                WITH (STATE = ON);
            GO
            """);

        Assert.DoesNotContain(
            report.SecurityPredicateIndexFindings,
            f => f.TableQualifiedName == "dbo.RlsIndexedTarget");
    }

    [Fact]
    public async Task LiveDeployment_DisabledPolicy_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE SCHEMA RlsSec;
            GO
            CREATE TABLE dbo.RlsDisabledTarget (Id INT NOT NULL PRIMARY KEY, TenantId INT NOT NULL, Payload NVARCHAR(50) NULL);
            GO
            CREATE FUNCTION RlsSec.fn_DisabledTenantPredicate(@TenantId INT)
            RETURNS TABLE
            WITH SCHEMABINDING
            AS
            RETURN SELECT 1 AS fn_result WHERE @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS INT);
            GO
            CREATE SECURITY POLICY RlsSec.DisabledTenantFilter
                ADD FILTER PREDICATE RlsSec.fn_DisabledTenantPredicate(TenantId) ON dbo.RlsDisabledTarget
                WITH (STATE = OFF);
            GO
            """);

        Assert.DoesNotContain(
            report.SecurityPredicateIndexFindings,
            f => f.TableQualifiedName == "dbo.RlsDisabledTarget");
    }
}
