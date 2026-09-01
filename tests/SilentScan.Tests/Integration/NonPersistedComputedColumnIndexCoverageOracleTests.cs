using System.Xml.Linq;
using SilentScan.Tests.Support;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

public sealed class NonPersistedComputedColumnIndexCoverageOracleTests : OracleTestFixture
{
    private const string ShowPlanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
    private const string CoveringIndexName = "IX_T1_Sum";

    protected override string DatabaseNameSeed => nameof(NonPersistedComputedColumnIndexCoverageOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.T1 (Id INT NOT NULL PRIMARY KEY, A INT NOT NULL, B INT NOT NULL, Sum AS (A + B));
        CREATE INDEX IX_T1_Sum ON dbo.T1 (Sum);
        INSERT INTO dbo.T1 (Id, A, B) VALUES (1, 12345, 0), (2, 100, 200), (3, 7, 8);
        """;

    [Fact]
    public async Task ReadServedThroughCoveringIndex_NeverTouchesBaseRow()
    {
        var planXml = await new PlanXmlCapture(Options).CaptureAsync(DatabaseName, "SELECT Sum FROM dbo.T1 WHERE Sum = 12345;");

        Assert.True(IndexAccessDetector.HasIndexSeek(planXml, CoveringIndexName));

        var doc = XDocument.Parse(planXml);
        var ns = (XNamespace)ShowPlanNs;
        var baseRowAccessOps = doc.Descendants(ns + "RelOp")
            .Where(relOp => (string?)relOp.Attribute("PhysicalOp") is { } op &&
                (op.Contains("Lookup", StringComparison.Ordinal) ||
                 op.Contains("Table Scan", StringComparison.Ordinal) ||
                 op == "Clustered Index Scan" ||
                 op == "Clustered Index Seek"))
            .ToList();

        Assert.Empty(baseRowAccessOps);
    }

    [Fact]
    public async Task ReadForcedOffTheCoveringIndex_StillRecomputesFromBaseRow()
    {
        var planXml = await new PlanXmlCapture(Options).CaptureAsync(
            DatabaseName, "SELECT Sum FROM dbo.T1 WITH (INDEX(0)) WHERE Sum = 12345;");

        Assert.False(IndexAccessDetector.HasIndexSeek(planXml, CoveringIndexName));

        var doc = XDocument.Parse(planXml);
        var ns = (XNamespace)ShowPlanNs;
        var scalarStrings = doc.Descendants(ns + "ScalarOperator")
            .Select(op => (string?)op.Attribute("ScalarString"))
            .Where(s => s is not null)
            .ToList();

        Assert.Contains(scalarStrings, s => s!.Contains("[A]", StringComparison.Ordinal) && s.Contains("[B]", StringComparison.Ordinal));
    }
}
