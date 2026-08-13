using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Live.Catalog;
using SilentScan.Tests.Support;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

/// <summary>
/// The live parity gate used to issue one <c>OBJECT_ID(@objectName)</c> round trip per resolved
/// relation, sequentially - thousands of serial round trips on a real database, where the gate's
/// wall-clock was network latency rather than work. It now reads every relevant object's columns
/// in ONE query and matches rows to relations by their <c>schema.object</c> key.
///
/// That rewrite has a specific, silent failure mode the old shape could not have: if the key the
/// read builds ever stops matching the key <see cref="SchemaObjectNameHelper.Qualify"/> produces
/// for lineage, EVERY relation is skipped as "not a real server object" and the gate reports zero
/// mismatches while checking nothing at all - a P0 gate that passes vacuously. Asserting
/// "no mismatches on a correct schema" cannot distinguish that from working correctly, so these
/// feed the checker a deliberately WRONG inferred type for a real deployed view and require the
/// mismatch to come back.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class LiveLineageParityCheckerBatchingTests
{
    private static readonly SqlServerOptions Options = SqlServerOptions.LocalDocker;

    [Fact]
    public async Task DeliberatelyWrongInferredType_IsReportedForADboView()
    {
        var mismatches = await CheckAsync(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL);
            GO
            CREATE VIEW dbo.vw_Orders AS SELECT OrderId FROM dbo.Orders;
            """,
            // vw_Orders.OrderId is really INT; claim VarChar so a working gate must object.
            lineageFor: "dbo.vw_Orders",
            columnName: "OrderId",
            inferredType: new SqlType(SqlTypeCategory.VarChar, Length: 50));

        var mismatch = Assert.Single(mismatches);
        Assert.Equal("dbo.vw_Orders", mismatch.QualifiedViewName);
        Assert.Equal("OrderId", mismatch.ColumnName);
        Assert.Equal("category", mismatch.Facet);
        Assert.Equal("VarChar", mismatch.InferredValue);
        Assert.Equal("int", mismatch.ActualValue);
    }

    [Fact]
    public async Task DeliberatelyWrongInferredType_IsReportedForANonDboSchemaView()
    {
        // The batched read builds its key from sys.schemas.name + '.' + sys.objects.name. A view
        // outside dbo is what catches a read that hardcoded dbo or dropped the schema entirely -
        // both of which still pass every dbo-only test.
        var mismatches = await CheckAsync(
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

        var mismatch = Assert.Single(mismatches);
        Assert.Equal("sales.vw_Invoices", mismatch.QualifiedViewName);
        Assert.Equal("Total", mismatch.ColumnName);
        Assert.Equal("money", mismatch.ActualValue);
    }

    [Fact]
    public async Task CorrectInferredType_ReportsNoMismatch()
    {
        // The near-miss for the two above: the same machinery, the same view, a RIGHT type. This
        // is what proves the mismatches above come from the comparison rather than from the gate
        // objecting to everything it reads.
        var mismatches = await CheckAsync(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL);
            GO
            CREATE VIEW dbo.vw_Orders AS SELECT OrderId FROM dbo.Orders;
            """,
            lineageFor: "dbo.vw_Orders",
            columnName: "OrderId",
            inferredType: new SqlType(SqlTypeCategory.Int));

        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task RelationThatIsNotARealServerObject_IsSkippedRatherThanReportedAsAMismatch()
    {
        // Derived tables and MSTVFs that never became a server object legitimately have no
        // sys.columns rows; absence must stay "nothing to compare", not a P0 mismatch.
        var mismatches = await CheckAsync(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL);
            GO
            CREATE VIEW dbo.vw_Orders AS SELECT OrderId FROM dbo.Orders;
            """,
            lineageFor: "dbo.vw_DoesNotExistOnTheServer",
            columnName: "OrderId",
            inferredType: new SqlType(SqlTypeCategory.VarChar, Length: 50));

        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task CyclicView_IsNotFetchedOrCompared()
    {
        // Cyclic views' inferred types are meaningless, so they are excluded from the wanted set
        // before the read rather than filtered afterward. A wrong type on one must stay silent.
        var lineage = BuildLineage(
            "dbo.vw_Orders", "OrderId", new SqlType(SqlTypeCategory.VarChar, Length: 50),
            cyclic: true);

        var mismatches = await RunAgainstDeployedAsync(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL);
            GO
            CREATE VIEW dbo.vw_Orders AS SELECT OrderId FROM dbo.Orders;
            """,
            lineage);

        Assert.Empty(mismatches);
    }

    private static Task<IReadOnlyList<LiveLineageParityMismatch>> CheckAsync(
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

    private static async Task<IReadOnlyList<LiveLineageParityMismatch>> RunAgainstDeployedAsync(string sql, LineageCatalog lineage)
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
