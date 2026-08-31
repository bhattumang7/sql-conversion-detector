using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;
using SilentScan.Verify.Catalog;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class IdentityRangeScannerDecimalPrecisionOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(IdentityRangeScannerDecimalPrecisionOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Precision28NearCeiling (Id DECIMAL(28,0) IDENTITY(0, 9500000000000000000000000000) NOT NULL PRIMARY KEY);
        GO
        INSERT INTO dbo.Precision28NearCeiling DEFAULT VALUES;
        INSERT INTO dbo.Precision28NearCeiling DEFAULT VALUES;
        GO
        CREATE TABLE dbo.Precision29FarFromCeiling (Id DECIMAL(29,0) IDENTITY(0, 75000000000000000000000000000) NOT NULL PRIMARY KEY);
        GO
        INSERT INTO dbo.Precision29FarFromCeiling DEFAULT VALUES;
        INSERT INTO dbo.Precision29FarFromCeiling DEFAULT VALUES;
        GO
        CREATE TABLE dbo.Precision38FarFromCeiling (Id DECIMAL(38,0) IDENTITY(0, 75000000000000000000000000000) NOT NULL PRIMARY KEY);
        GO
        INSERT INTO dbo.Precision38FarFromCeiling DEFAULT VALUES;
        INSERT INTO dbo.Precision38FarFromCeiling DEFAULT VALUES;
        """;

    [Fact]
    public async Task Precision28IdentityNearCeiling_DoesNotCrashAndFires()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var findings = IdentityRangeScanner.Scan(catalog);

        var finding = Assert.Single(findings, f => f.TableQualifiedName.Contains("Precision28NearCeiling", StringComparison.Ordinal));
        Assert.Equal(IdentityRangeFindingKind.IdentityRangeNearExhaustion, finding.Kind);
        Assert.Contains("9999999999999999999999999999", finding.DetailText);
    }

    [Fact]
    public async Task Precision29IdentityFarFromTrueCeiling_DoesNotCrashAndDoesNotFire()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var findings = IdentityRangeScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.TableQualifiedName.Contains("Precision29FarFromCeiling", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Precision38IdentityFarFromTrueCeiling_DoesNotCrashAndDoesNotFire()
    {
        var catalog = await new LiveCatalogReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var findings = IdentityRangeScanner.Scan(catalog);

        Assert.DoesNotContain(findings, f => f.TableQualifiedName.Contains("Precision38FarFromCeiling", StringComparison.Ordinal));
    }
}
