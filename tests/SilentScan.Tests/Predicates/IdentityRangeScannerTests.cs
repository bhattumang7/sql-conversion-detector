using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §A "Identity/sequence range
/// exhaustion" - see <see cref="IdentityRangeFinding"/> for the full scope/precision story,
/// including why the two kinds are split along the schema-decidable/data-state-decidable axis.
/// <see cref="CatalogColumn"/>'s identity fields are live-only, populated only by
/// <see cref="SilentScan.Verify.Catalog.LiveCatalogReader"/> - these tests build the catalog
/// directly, the same shape <c>IndexDesignScannerTests</c> already established for a live-only-
/// input scanner.
/// </summary>
public sealed class IdentityRangeScannerTests
{
    private static CatalogTable Table(string schema, string name, IReadOnlyList<CatalogColumn> columns) =>
        new(schema, name, CatalogTableKind.Table, columns, [], SourcePath: $"{schema}.{name}", SourceLine: 1);

    private static CatalogColumn IdentityColumn(
        string name, SqlType type, decimal? seed, decimal? increment, decimal? currentValue) =>
        new(name, type, IsNullable: false, IsIdentity: true, IsComputed: false, IsPersisted: false,
            IdentitySeed: seed, IdentityIncrement: increment, IdentityCurrentValue: currentValue);

    private static readonly SqlType IntType = new(SqlTypeCategory.Int);
    private static readonly SqlType TinyIntType = new(SqlTypeCategory.TinyInt);

    [Fact]
    public void NegativeSeed_FiresAnomalyAtLowConfidence()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [IdentityColumn("Id", IntType, seed: -1, increment: 1, currentValue: 5)]));

        var findings = IdentityRangeScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.Kind == IdentityRangeFindingKind.IdentitySeedOrIncrementAnomaly);
        Assert.Equal(FindingConfidence.Low, finding.Confidence);
    }

    [Fact]
    public void NonOneIncrement_FiresAnomalyAtLowConfidence()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [IdentityColumn("Id", IntType, seed: 1, increment: 2, currentValue: 5)]));

        var findings = IdentityRangeScanner.Scan(catalog);

        Assert.Single(findings, f => f.Kind == IdentityRangeFindingKind.IdentitySeedOrIncrementAnomaly);
    }

    [Fact]
    public void OrdinarySeedAndIncrement_NeverFiresAnomaly()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [IdentityColumn("Id", IntType, seed: 1, increment: 1, currentValue: 5)]));

        var findings = IdentityRangeScanner.Scan(catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void NearTypeCeiling_FiresExhaustion()
    {
        var catalog = new DatabaseCatalog();
        // tinyint maxes at 255 - a current value of 250 has consumed (250-0)/(255-0) ≈ 98%.
        catalog.AddOrReplace(Table("dbo", "Orders", [IdentityColumn("Id", TinyIntType, seed: 0, increment: 1, currentValue: 250)]));

        var findings = IdentityRangeScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.Kind == IdentityRangeFindingKind.IdentityRangeNearExhaustion);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("PRODUCTION-SHAPED", finding.DetailText);
    }

    [Fact]
    public void LowValueFarFromCeiling_NeverFiresExhaustion()
    {
        // The checklist's own example: "a dev copy with an identity at 400 is not evidence" -
        // int's own ceiling is billions away, so this must never fire regardless of database
        // shape.
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [IdentityColumn("Id", IntType, seed: 1, increment: 1, currentValue: 400)]));

        var findings = IdentityRangeScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IdentityRangeFindingKind.IdentityRangeNearExhaustion);
    }

    [Fact]
    public void NoRowsEverInserted_CurrentValueNull_NeverFiresExhaustion()
    {
        var catalog = new DatabaseCatalog();
        catalog.AddOrReplace(Table("dbo", "Orders", [IdentityColumn("Id", TinyIntType, seed: 0, increment: 1, currentValue: null)]));

        var findings = IdentityRangeScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.Kind == IdentityRangeFindingKind.IdentityRangeNearExhaustion);
    }

    [Fact]
    public void NonIdentityColumn_NeverFires()
    {
        var catalog = new DatabaseCatalog();
        var column = new CatalogColumn("Id", IntType, IsNullable: false, IsIdentity: false, IsComputed: false, IsPersisted: false);
        catalog.AddOrReplace(Table("dbo", "Orders", [column]));

        var findings = IdentityRangeScanner.Scan(catalog);

        Assert.Empty(findings);
    }

    [Fact]
    public void DescendingIdentity_NearTypeFloor_FiresExhaustion()
    {
        var catalog = new DatabaseCatalog();
        // tinyint's own minimum is 0 - a negative increment descends toward it; seed 255,
        // increment -1, current value 5 has consumed (255-5)/(255-0) ≈ 98% toward 0.
        catalog.AddOrReplace(Table("dbo", "Orders", [IdentityColumn("Id", TinyIntType, seed: 255, increment: -1, currentValue: 5)]));

        var findings = IdentityRangeScanner.Scan(catalog);

        Assert.Single(findings, f => f.Kind == IdentityRangeFindingKind.IdentityRangeNearExhaustion);
    }
}
