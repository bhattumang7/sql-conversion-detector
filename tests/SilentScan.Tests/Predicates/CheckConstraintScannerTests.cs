using System.Text.Json;
using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;
using SilentScan.Tests.Support;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
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

        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", []));
        catalog.AddCheckConstraint(new CatalogCheckConstraint(
            "CK_Orders_Ghost", "dbo.Orders", IsNotTrusted: false, IsDisabled: false, DefinitionText: "([Ghost]>(0))"));

        Assert.Empty(CheckConstraintScanner.Scan(catalog));
    }

    [Fact]
    public async Task LiveDeployment_NullableColumnCheckWithNoGuard_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            "CREATE TABLE dbo.CheckNullTarget (Id INT NOT NULL PRIMARY KEY, Price DECIMAL(10,2) NULL, CONSTRAINT CK_CheckNullTarget_Price CHECK (Price > 0));");

        var finding = Assert.Single(report.Find<CheckConstraintFinding>("CheckConstraintScanner"), f => f.Kind == CheckConstraintFindingKind.NullNotHandled);
        Assert.Equal("CK_CheckNullTarget_Price", finding.ConstraintName);
        Assert.Equal("Price", finding.ColumnName);
    }

    [Fact]
    public async Task LiveDeployment_NullableColumnCheckWithOrGuard_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            "CREATE TABLE dbo.CheckNullGuarded (Id INT NOT NULL PRIMARY KEY, Price DECIMAL(10,2) NULL, CONSTRAINT CK_CheckNullGuarded_Price CHECK (Price IS NULL OR Price > 0));");

        Assert.DoesNotContain(report.Find<CheckConstraintFinding>("CheckConstraintScanner"), f => f.Kind == CheckConstraintFindingKind.NullNotHandled);
    }

    [Fact]
    public async Task LiveDeployment_IdentityColumnCheck_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            "CREATE TABLE dbo.CheckIdentityTarget (Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY, CONSTRAINT CK_CheckIdentityTarget_Id CHECK (Id > 5), Name VARCHAR(20) NULL);");

        var finding = Assert.Single(report.Find<CheckConstraintFinding>("CheckConstraintScanner"), f => f.Kind == CheckConstraintFindingKind.ConstraintOnIdentityColumn);
        Assert.Equal("CK_CheckIdentityTarget_Id", finding.ConstraintName);
        Assert.Equal("Id", finding.ColumnName);
    }

    private static string SarifMessageFor(SilentScan.Core.Reporting.ScanReport report, string constraintName)
    {
        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);
        var results = document.RootElement.GetProperty("runs")[0].GetProperty("results");
        foreach (var result in results.EnumerateArray())
        {
            var text = result.GetProperty("message").GetProperty("text").GetString()!;
            if (text.Contains($"'{constraintName}'", StringComparison.Ordinal))
            {
                return text;
            }
        }

        throw new InvalidOperationException($"No SARIF result found for constraint '{constraintName}'.");
    }

    [Fact]
    public async Task LiveDeployment_IncreasingThresholdIdentityCheck_MessageClaimsPermanentSilentSatisfaction()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            "CREATE TABLE dbo.CheckIdentityIncreasing (Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY, CONSTRAINT CK_CheckIdentityIncreasing_Id CHECK (Id > 5), Name VARCHAR(20) NULL);");

        var finding = Assert.Single(report.Find<CheckConstraintFinding>("CheckConstraintScanner"), f => f.Kind == CheckConstraintFindingKind.ConstraintOnIdentityColumn);
        Assert.Equal(IdentityCheckThresholdDirection.Increasing, finding.ThresholdDirection);

        var message = SarifMessageFor(report, "CK_CheckIdentityIncreasing_Id");
        Assert.Contains("catches up and failures silently stop forever", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveDeployment_DecreasingThresholdIdentityCheck_MessageDoesNotClaimStopsMatteringForever()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            "CREATE TABLE dbo.CheckIdentityDecreasing (Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY, CONSTRAINT CK_CheckIdentityDecreasing_Id CHECK (Id < 5), Name VARCHAR(20) NULL);");

        var finding = Assert.Single(report.Find<CheckConstraintFinding>("CheckConstraintScanner"), f => f.Kind == CheckConstraintFindingKind.ConstraintOnIdentityColumn);
        Assert.Equal(IdentityCheckThresholdDirection.Decreasing, finding.ThresholdDirection);

        var message = SarifMessageFor(report, "CK_CheckIdentityDecreasing_Id");
        Assert.DoesNotContain("stop forever", message, StringComparison.Ordinal);
        Assert.DoesNotContain("catches up and failures silently stop forever", message, StringComparison.Ordinal);
        Assert.Contains("permanently", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveDeployment_PeriodicPredicateIdentityCheck_MessageDoesNotClaimDeterministicSettlement()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            "CREATE TABLE dbo.CheckIdentityPeriodic (Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY, CONSTRAINT CK_CheckIdentityPeriodic_Id CHECK (Id % 2 = 0), Name VARCHAR(20) NULL);");

        var finding = Assert.Single(report.Find<CheckConstraintFinding>("CheckConstraintScanner"), f => f.Kind == CheckConstraintFindingKind.ConstraintOnIdentityColumn);
        Assert.Equal(IdentityCheckThresholdDirection.Other, finding.ThresholdDirection);

        var message = SarifMessageFor(report, "CK_CheckIdentityPeriodic_Id");
        Assert.DoesNotContain("stop forever", message, StringComparison.Ordinal);
        Assert.DoesNotContain("catches up", message, StringComparison.Ordinal);
    }
}
