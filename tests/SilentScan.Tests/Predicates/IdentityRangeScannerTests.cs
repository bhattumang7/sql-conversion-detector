using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates;

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
    public void NearTypeCeiling_FiresExhaustion()
    {
        var catalog = new DatabaseCatalog();

        catalog.AddOrReplace(Table("dbo", "Orders", [IdentityColumn("Id", TinyIntType, seed: 0, increment: 1, currentValue: 250)]));

        var findings = IdentityRangeScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.Kind == IdentityRangeFindingKind.IdentityRangeNearExhaustion);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Contains("PRODUCTION-SHAPED", finding.DetailText);
    }

    [Fact]
    public void LowValueFarFromCeiling_NeverFiresExhaustion()
    {

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

        catalog.AddOrReplace(Table("dbo", "Orders", [IdentityColumn("Id", TinyIntType, seed: 255, increment: -1, currentValue: 5)]));

        var findings = IdentityRangeScanner.Scan(catalog);

        Assert.Single(findings, f => f.Kind == IdentityRangeFindingKind.IdentityRangeNearExhaustion);
    }
}
