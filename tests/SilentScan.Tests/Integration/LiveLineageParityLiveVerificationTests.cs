using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Live.Catalog;
using SilentScan.Tests.Support;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

/// <summary>
/// Covers the three outcomes <see cref="LiveLineageParityChecker"/> added on top of a plain
/// mismatch: a stale cached <c>sys.columns</c> row that agrees with the live answer (not a bug),
/// an object the server can no longer compile at all (not a bug), and a column that could not be
/// live-verified at all (an inline TVF parameter with no typed-NULL form). Each test deploys real
/// DDL to the disposable Docker instance and, where a fixture needs to induce staleness, ALTERs
/// the base column's type AFTER the view/function was created - the exact real-world sequence
/// that leaves a view's own cached metadata behind the engine's live answer.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class LiveLineageParityLiveVerificationTests
{
    private static readonly SqlServerOptions Options = SqlServerOptions.LocalDocker;

    [Fact]
    public async Task StaleCachedMetadata_BaseColumnRetypedAfterTheViewWasCreated_IsNotAMismatch()
    {
        // The view's own sys.columns row still says tinyint (snapshotted at CREATE VIEW time);
        // the live DMV answer is int. The inferred type below is the CURRENT, correct answer -
        // exactly what this tool's own base-table-driven inference would produce today.
        var report = await CheckAsync(
            """
            CREATE TABLE dbo.Orders (Amount TINYINT NOT NULL);
            GO
            CREATE VIEW dbo.vw_Orders AS SELECT Amount FROM dbo.Orders;
            GO
            ALTER TABLE dbo.Orders ALTER COLUMN Amount INT;
            """,
            lineageFor: "dbo.vw_Orders",
            columnName: "Amount",
            inferredType: new SqlType(SqlTypeCategory.Int));

        Assert.Empty(report.Mismatches);
        var stale = Assert.Single(report.StaleCachedMetadata);
        Assert.Equal("dbo.vw_Orders", stale.QualifiedViewName);
        Assert.Equal("Amount", stale.ColumnName);
        Assert.Equal("tinyint", stale.CachedValue);
        Assert.Equal("int", stale.LiveValue);
        Assert.Empty(report.UncompilableObjects);
        Assert.Empty(report.Unverified);
    }

    [Fact]
    public async Task GenuineInferenceBug_OnAStaleObject_IsStillReportedAsAMismatch()
    {
        // Same staleness-inducing schema as above, but this time the inferred type is
        // deliberately wrong - proving a real bug still surfaces as a P0 even on an object whose
        // cache also happens to be stale, and that the reported "actual" value is the LIVE
        // answer (int), never the stale cached one (tinyint).
        var report = await CheckAsync(
            """
            CREATE TABLE dbo.Orders (Amount TINYINT NOT NULL);
            GO
            CREATE VIEW dbo.vw_Orders AS SELECT Amount FROM dbo.Orders;
            GO
            ALTER TABLE dbo.Orders ALTER COLUMN Amount INT;
            """,
            lineageFor: "dbo.vw_Orders",
            columnName: "Amount",
            inferredType: new SqlType(SqlTypeCategory.VarChar, Length: 50));

        var mismatch = Assert.Single(report.Mismatches);
        Assert.Equal("dbo.vw_Orders", mismatch.QualifiedViewName);
        Assert.Equal("int", mismatch.ActualValue);
        Assert.DoesNotContain(report.StaleCachedMetadata, s => s.ColumnName == "Amount");
    }

    [Fact]
    public async Task UncompilableView_IsReportedAsUncompilable_NotAsAMismatch()
    {
        // Dropping a column a non-schema-bound view references leaves the view deployed but
        // broken - the server can no longer describe it at all. A deliberately wrong inferred
        // type on the SURVIVING column must still not produce a P0: there is nothing live to
        // compare it against.
        var report = await CheckAsync(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL, Total INT NOT NULL);
            GO
            CREATE VIEW dbo.vw_Orders AS SELECT OrderId, Total FROM dbo.Orders;
            GO
            ALTER TABLE dbo.Orders DROP COLUMN Total;
            """,
            lineageFor: "dbo.vw_Orders",
            columnName: "OrderId",
            inferredType: new SqlType(SqlTypeCategory.VarChar, Length: 50));

        Assert.Empty(report.Mismatches);
        var broken = Assert.Single(report.UncompilableObjects);
        Assert.Equal("dbo.vw_Orders", broken.QualifiedViewName);
        Assert.NotEqual(0, broken.ErrorNumber);
        Assert.False(string.IsNullOrWhiteSpace(broken.ErrorMessage));
    }

    [Fact]
    public async Task InlineTvf_WithParameters_IsLiveVerified()
    {
        var report = await CheckAsync(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL, Amount MONEY NOT NULL);
            GO
            CREATE FUNCTION dbo.fn_OrdersAbove(@minAmount MONEY)
            RETURNS TABLE AS RETURN
                SELECT OrderId, Amount FROM dbo.Orders WHERE Amount > @minAmount;
            """,
            lineageFor: "dbo.fn_OrdersAbove",
            columnName: "OrderId",
            inferredType: new SqlType(SqlTypeCategory.VarChar, Length: 50));

        var mismatch = Assert.Single(report.Mismatches);
        Assert.Equal("dbo.fn_OrdersAbove", mismatch.QualifiedViewName);
        Assert.Equal("int", mismatch.ActualValue);
    }

    [Fact]
    public async Task InlineTvf_WithATableValuedParameter_IsReportedAsUnverified()
    {
        // A TVP has no typed-NULL form a bare probe SELECT can supply, so this function can
        // never be live-verified - the disagreement below must land as "could not verify", not
        // as a P0, and nothing may crash trying to build the probe.
        var report = await CheckAsync(
            """
            CREATE TYPE dbo.IntListType AS TABLE (Value INT NOT NULL);
            GO
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL);
            GO
            CREATE FUNCTION dbo.fn_FilterOrders(@ids dbo.IntListType READONLY)
            RETURNS TABLE AS RETURN
                SELECT OrderId FROM dbo.Orders WHERE OrderId IN (SELECT Value FROM @ids);
            """,
            lineageFor: "dbo.fn_FilterOrders",
            columnName: "OrderId",
            inferredType: new SqlType(SqlTypeCategory.VarChar, Length: 50));

        Assert.Empty(report.Mismatches);
        var unverified = Assert.Single(report.Unverified);
        Assert.Equal("dbo.fn_FilterOrders", unverified.QualifiedViewName);
        Assert.Contains("table-valued parameter", unverified.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultiStatementTvf_MismatchIsStillReportedDirectlyAgainstItsAuthoredShape()
    {
        // A multi-statement TVF's shape is its own authored RETURNS @t TABLE(...) clause - one
        // source, so it can't go stale, and this checker never live-probes it. Pins that design
        // decision so a future change can't silently start (or stop) probing them.
        var report = await CheckAsync(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL);
            GO
            CREATE FUNCTION dbo.fn_GetOrders()
            RETURNS @result TABLE (OrderId INT)
            AS
            BEGIN
                INSERT INTO @result SELECT OrderId FROM dbo.Orders;
                RETURN;
            END;
            """,
            lineageFor: "dbo.fn_GetOrders",
            columnName: "OrderId",
            inferredType: new SqlType(SqlTypeCategory.VarChar, Length: 50));

        var mismatch = Assert.Single(report.Mismatches);
        Assert.Equal("dbo.fn_GetOrders", mismatch.QualifiedViewName);
        Assert.Equal("int", mismatch.ActualValue);
        Assert.Empty(report.UncompilableObjects);
        Assert.Empty(report.StaleCachedMetadata);
        Assert.Empty(report.Unverified);
    }

    private static Task<LiveLineageParityReport> CheckAsync(
        string sql, string lineageFor, string columnName, SqlType inferredType) =>
        RunAgainstDeployedAsync(sql, BuildLineage(lineageFor, columnName, inferredType));

    private static LineageCatalog BuildLineage(string qualifiedName, string columnName, SqlType inferredType)
    {
        var relation = new ResolvedRelation(
            qualifiedName,
            [new ResolvedColumn(columnName, new ColumnProvenance.Declared(inferredType, qualifiedName))]);

        return new LineageCatalog(
            new Dictionary<string, ResolvedRelation>(StringComparer.OrdinalIgnoreCase) { [qualifiedName] = relation },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
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
