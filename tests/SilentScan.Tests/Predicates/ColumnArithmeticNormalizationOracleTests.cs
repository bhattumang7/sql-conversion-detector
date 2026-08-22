using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class ColumnArithmeticNormalizationOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(ColumnArithmeticNormalizationOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Products (ProductId INT NOT NULL PRIMARY KEY, UnitPrice INT NOT NULL);
        GO
        CREATE INDEX IX_Products_UnitPrice ON dbo.Products(UnitPrice);
        """;

    [Fact]
    public async Task AdditiveIdentityOnIndexedColumn_DoesNotSeek()
    {
        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(
            DatabaseName, "SELECT ProductId FROM dbo.Products WHERE UnitPrice + 0 = 5;");

        Assert.DoesNotContain("PhysicalOp=\"Index Seek\"", planXml);
    }
}
