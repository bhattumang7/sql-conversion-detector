using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Live.Catalog;
using SilentScan.Tests.Support;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class LiveLineageParityCheckerBatchingTests
{
    private static readonly SqlServerOptions Options = SqlServerOptions.LocalDocker;

    [Fact]
    public async Task DeliberatelyWrongInferredType_IsReportedForADboView()
    {
        var report = await CheckAsync(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL);
            GO
            CREATE VIEW dbo.vw_Orders AS SELECT OrderId FROM dbo.Orders;
            """,

            lineageFor: "dbo.vw_Orders",
            columnName: "OrderId",
            inferredType: new SqlType(SqlTypeCategory.VarChar, Length: 50));

        var mismatch = Assert.Single(report.Mismatches);
        Assert.Equal("dbo.vw_Orders", mismatch.QualifiedViewName);
        Assert.Equal("OrderId", mismatch.ColumnName);
        Assert.Equal("category", mismatch.Facet);
        Assert.Equal("VarChar", mismatch.InferredValue);
        Assert.Equal("int", mismatch.ActualValue);
        AssertOtherBucketsEmpty(report);
    }

    [Fact]
    public async Task DeliberatelyWrongInferredType_IsReportedForANonDboSchemaView()
    {

        var report = await CheckAsync(
            """
            CREATE SCHEMA sales;
            GO
            CREATE TABLE sales.Invoices (Total MONEY NOT NULL);
            GO
            CREATE VIEW sales.vw_Invoices AS SELECT Total FROM sales.Invoices;
            """,
            lineageFor: "sales.vw_Invoices",
            columnName: "Total",
            inferredType: new SqlType(SqlTypeCategory.Int));

        var mismatch = Assert.Single(report.Mismatches);
        Assert.Equal("sales.vw_Invoices", mismatch.QualifiedViewName);
        Assert.Equal("Total", mismatch.ColumnName);
        Assert.Equal("money", mismatch.ActualValue);
        AssertOtherBucketsEmpty(report);
    }

    [Fact]
    public async Task CorrectInferredType_ReportsNoMismatch()
    {

        var report = await CheckAsync(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL);
            GO
            CREATE VIEW dbo.vw_Orders AS SELECT OrderId FROM dbo.Orders;
            """,
            lineageFor: "dbo.vw_Orders",
            columnName: "OrderId",
            inferredType: new SqlType(SqlTypeCategory.Int));

        Assert.Empty(report.Mismatches);
        AssertOtherBucketsEmpty(report);
    }

    [Fact]
    public async Task RelationThatIsNotARealServerObject_IsSkippedRatherThanReportedAsAMismatch()
    {

        var report = await CheckAsync(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL);
            GO
            CREATE VIEW dbo.vw_Orders AS SELECT OrderId FROM dbo.Orders;
            """,
            lineageFor: "dbo.vw_DoesNotExistOnTheServer",
            columnName: "OrderId",
            inferredType: new SqlType(SqlTypeCategory.VarChar, Length: 50));

        Assert.Empty(report.Mismatches);
        AssertOtherBucketsEmpty(report);
    }

    [Fact]
    public async Task CyclicView_IsNotFetchedOrCompared()
    {

        var lineage = BuildLineage(
            "dbo.vw_Orders", "OrderId", new SqlType(SqlTypeCategory.VarChar, Length: 50),
            cyclic: true);

        var report = await RunAgainstDeployedAsync(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL);
            GO
            CREATE VIEW dbo.vw_Orders AS SELECT OrderId FROM dbo.Orders;
            """,
            lineage);

        Assert.Empty(report.Mismatches);
        AssertOtherBucketsEmpty(report);
    }

    private static void AssertOtherBucketsEmpty(LiveLineageParityReport report)
    {
        Assert.Empty(report.UncompilableObjects);
        Assert.Empty(report.StaleCachedMetadata);
        Assert.Empty(report.Unverified);
    }

    private static Task<LiveLineageParityReport> CheckAsync(
        string sql, string lineageFor, string columnName, SqlType inferredType) =>
        RunAgainstDeployedAsync(sql, BuildLineage(lineageFor, columnName, inferredType, cyclic: false));

    private static LineageCatalog BuildLineage(string qualifiedName, string columnName, SqlType inferredType, bool cyclic)
    {
        var relation = new ResolvedRelation(
            qualifiedName,
            [new ResolvedColumn(columnName, new ColumnProvenance.Declared(inferredType, qualifiedName))]);

        return new LineageCatalog(
            new Dictionary<string, ResolvedRelation>(StringComparer.OrdinalIgnoreCase) { [qualifiedName] = relation },
            cyclic
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { qualifiedName }
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new SkipLedger());
    }

    private static async Task<LiveLineageParityReport> RunAgainstDeployedAsync(string sql, LineageCatalog lineage)
    {
        var databaseName = $"SilentScanTest_{Guid.NewGuid():N}";
        var provisioner = new DatabaseProvisioner(Options);
        await provisioner.CreateFreshAsync(databaseName);
        try
        {
            await new ScriptDeployer(Options).DeployAsync(sql, databaseName);
            return await new LiveLineageParityChecker(Options.BuildConnectionString(databaseName)).CheckAsync(lineage);
        }
        finally
        {
            await provisioner.DropIfExistsAsync(databaseName);
        }
    }
}
