using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 §A: "CHECK constraint that doesn't account for NULL" /
/// "CHECK constraint accidentally placed on an IDENTITY column". <see
/// cref="CatalogCheckConstraint.DefinitionText"/> is only ever populated live - these tests build
/// the catalog directly, the same "no Docker oracle needed to exercise the scanner's own logic"
/// shape <c>UntrustedConstraintScannerTests</c>/<c>IndexDesignScannerTests</c>'s
/// <c>FilterColumnNotInIndex</c> cases already established for a text-reparsing, live-only-input
/// scanner; the engine mechanics themselves were separately verified against the real standing
/// Docker oracle (docs/detection-checklist.md carries that evidence).
/// </summary>
public sealed class CheckConstraintScannerTests
{
    private static CatalogTable Table(string schema, string name, IReadOnlyList<CatalogColumn> columns) =>
        new(schema, name, CatalogTableKind.Table, columns, [], SourcePath: $"{schema}.{name}", SourceLine: 1);

    private static CatalogColumn Column(string name, bool isNullable = false, bool isIdentity = false) =>
        new(name, new SqlType(SqlTypeCategory.Int), isNullable, isIdentity, IsComputed: false, IsPersisted: false);

    [Fact]
    public void NullableColumn_PredicateWithNoNullGuard_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Price", isNullable: true)]));
        catalog.AddCheckConstraint(new CatalogCheckConstraint(
            "CK_Orders_Price", "dbo.Orders", IsNotTrusted: false, IsDisabled: false, DefinitionText: "([Price]>(0))"));

        var findings = CheckConstraintScanner.Scan(catalog);

        var finding = Assert.Single(findings);
        Assert.Equal(CheckConstraintFindingKind.NullNotHandled, finding.Kind);
        Assert.Equal("CK_Orders_Price", finding.ConstraintName);
        Assert.Equal("Price", finding.ColumnName);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void NullableColumn_PredicateWithOrBranchNullGuard_NeverFires()
    {
        // The textbook fix - CHECK (Price IS NULL OR Price > 0) - oracle-confirmed to genuinely
        // accept a NULL row while still rejecting a negative one. A guard reachable through an OR
        // branch must still count as "handled": the inverse of the AND-only-reachable discipline
        // other scanners in this codebase apply for triggering, deliberately liberal here since
        // this kind's own risk is the ABSENCE of a guard.
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Price", isNullable: true)]));
        catalog.AddCheckConstraint(new CatalogCheckConstraint(
            "CK_Orders_Price", "dbo.Orders", IsNotTrusted: false, IsDisabled: false,
            DefinitionText: "([Price] IS NULL OR [Price]>(0))"));

        var findings = CheckConstraintScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == CheckConstraintFindingKind.NullNotHandled);
    }

    [Fact]
    public void NullableColumn_PredicateWithIsNotNullGuard_NeverFires()
    {
        // IS NOT NULL is still a bare IS NULL-family test against the column - it proves the
        // author was explicitly reasoning about the NULL case, not merely a coincidental omission.
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Price", isNullable: true)]));
        catalog.AddCheckConstraint(new CatalogCheckConstraint(
            "CK_Orders_Price", "dbo.Orders", IsNotTrusted: false, IsDisabled: false,
            DefinitionText: "([Price] IS NOT NULL AND [Price]>(0))"));

        var findings = CheckConstraintScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == CheckConstraintFindingKind.NullNotHandled);
    }

    [Fact]
    public void NotNullColumn_NoNullGuardNeeded_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Price", isNullable: false)]));
        catalog.AddCheckConstraint(new CatalogCheckConstraint(
            "CK_Orders_Price", "dbo.Orders", IsNotTrusted: false, IsDisabled: false, DefinitionText: "([Price]>(0))"));

        var findings = CheckConstraintScanner.Scan(catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void MultiColumnTableLevelConstraint_OnlyUnguardedNullableColumnFires()
    {
        // Table-level constraint (sys.check_constraints.parent_column_id = 0 for this shape,
        // confirmed directly against the oracle) - both columns are referenced, but only the
        // nullable, unguarded one should fire.
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table(
            "dbo", "Orders",
            [Column("A", isNullable: true), Column("B", isNullable: false)]));
        catalog.AddCheckConstraint(new CatalogCheckConstraint(
            "CK_T3", "dbo.Orders", IsNotTrusted: false, IsDisabled: false, DefinitionText: "([A]>[B])"));

        var findings = CheckConstraintScanner.Scan(catalog);

        var finding = Assert.Single(findings);
        Assert.Equal(CheckConstraintFindingKind.NullNotHandled, finding.Kind);
        Assert.Equal("A", finding.ColumnName);
    }

    [Fact]
    public void IdentityColumn_ReferencedByCheck_Fires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", isNullable: false, isIdentity: true)]));
        catalog.AddCheckConstraint(new CatalogCheckConstraint(
            "CK_Orders_Id", "dbo.Orders", IsNotTrusted: false, IsDisabled: false, DefinitionText: "([Id]>(5))"));

        var findings = CheckConstraintScanner.Scan(catalog);

        var finding = Assert.Single(findings);
        Assert.Equal(CheckConstraintFindingKind.ConstraintOnIdentityColumn, finding.Kind);
        Assert.Equal("CK_Orders_Id", finding.ConstraintName);
        Assert.Equal("Id", finding.ColumnName);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public void NonIdentityColumn_NeverFiresIdentityKind()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Id", isNullable: false, isIdentity: false)]));
        catalog.AddCheckConstraint(new CatalogCheckConstraint(
            "CK_Orders_Id", "dbo.Orders", IsNotTrusted: false, IsDisabled: false, DefinitionText: "([Id]>(5))"));

        var findings = CheckConstraintScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == CheckConstraintFindingKind.ConstraintOnIdentityColumn);
    }

    [Fact]
    public void DisabledConstraint_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Price", isNullable: true)]));
        catalog.AddCheckConstraint(new CatalogCheckConstraint(
            "CK_Orders_Price", "dbo.Orders", IsNotTrusted: false, IsDisabled: true, DefinitionText: "([Price]>(0))"));

        Assert.Empty(CheckConstraintScanner.Scan(catalog));
    }

    [Fact]
    public void UnparseableDefinitionText_NeverGuesses()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Price", isNullable: true)]));
        catalog.AddCheckConstraint(new CatalogCheckConstraint(
            "CK_Orders_Price", "dbo.Orders", IsNotTrusted: false, IsDisabled: false, DefinitionText: "not valid t-sql at all ((("));

        Assert.Empty(CheckConstraintScanner.Scan(catalog));
    }

    [Fact]
    public void EmptyDefinitionText_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [Column("Price", isNullable: true)]));
        catalog.AddCheckConstraint(new CatalogCheckConstraint(
            "CK_Orders_Price", "dbo.Orders", IsNotTrusted: false, IsDisabled: false, DefinitionText: ""));

        Assert.Empty(CheckConstraintScanner.Scan(catalog));
    }

    [Fact]
    public void UnknownColumnReferencedInDefinition_IsSkippedNotGuessed()
    {
        // A column the catalog doesn't know about (a computed expression, a typo the engine would
        // itself reject at DDL time, or simply a column this pass' own catalog build didn't carry
        // for some other reason) - never guess a nullability/identity verdict for it.
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", []));
        catalog.AddCheckConstraint(new CatalogCheckConstraint(
            "CK_Orders_Ghost", "dbo.Orders", IsNotTrusted: false, IsDisabled: false, DefinitionText: "([Ghost]>(0))"));

        Assert.Empty(CheckConstraintScanner.Scan(catalog));
    }

    /// <summary>
    /// End-to-end against the real standing Docker oracle (a fresh, disposable database, dropped
    /// unconditionally afterward) rather than a hand-built catalog - proves the full live-read path
    /// (<c>LiveCatalogReader</c>'s new <see cref="CatalogCheckConstraint.DefinitionText"/> column,
    /// through <see cref="CheckConstraintScanner"/>, into the real <see
    /// cref="SilentScan.Core.Reporting.ScanReport.CheckConstraintFindings"/> stream) works against a real
    /// <c>sys.check_constraints.definition</c> string, not just the hand-authored text used by the
    /// catalog-builder tests above.
    /// </summary>
    [Fact]
    public async Task LiveDeployment_NullableColumnCheckWithNoGuard_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            "CREATE TABLE dbo.CheckNullTarget (Id INT NOT NULL PRIMARY KEY, Price DECIMAL(10,2) NULL, CONSTRAINT CK_CheckNullTarget_Price CHECK (Price > 0));");

        var finding = Assert.Single(report.CheckConstraintFindings, f => f.Kind == CheckConstraintFindingKind.NullNotHandled);
        Assert.Equal("CK_CheckNullTarget_Price", finding.ConstraintName);
        Assert.Equal("Price", finding.ColumnName);
    }

    [Fact]
    public async Task LiveDeployment_NullableColumnCheckWithOrGuard_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            "CREATE TABLE dbo.CheckNullGuarded (Id INT NOT NULL PRIMARY KEY, Price DECIMAL(10,2) NULL, CONSTRAINT CK_CheckNullGuarded_Price CHECK (Price IS NULL OR Price > 0));");

        Assert.DoesNotContain(report.CheckConstraintFindings, f => f.Kind == CheckConstraintFindingKind.NullNotHandled);
    }

    [Fact]
    public async Task LiveDeployment_IdentityColumnCheck_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            "CREATE TABLE dbo.CheckIdentityTarget (Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY, CONSTRAINT CK_CheckIdentityTarget_Id CHECK (Id > 5), Name VARCHAR(20) NULL);");

        var finding = Assert.Single(report.CheckConstraintFindings, f => f.Kind == CheckConstraintFindingKind.ConstraintOnIdentityColumn);
        Assert.Equal("CK_CheckIdentityTarget_Id", finding.ConstraintName);
        Assert.Equal("Id", finding.ColumnName);
    }
}
