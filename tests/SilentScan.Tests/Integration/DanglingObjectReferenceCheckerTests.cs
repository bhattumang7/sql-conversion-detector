using SilentScan.Core.Predicates;
using SilentScan.Live.Catalog;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

/// <summary>
/// Oracle-confirmed directly against the local Docker instance: a stored procedure referencing a
/// table that was never created compiles clean (SQL Server defers name resolution for a module
/// body until it runs) and only fails with Msg 208 ("Invalid object name") at EXEC time - these
/// tests assert the checker surfaces that gap from the catalog alone, without ever executing the
/// broken module itself.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class DanglingObjectReferenceCheckerTests
{
    private static readonly SqlServerOptions Options = SqlServerOptions.LocalDocker;

    [Fact]
    public async Task ProcedureReferencingNonexistentTable_IsReported()
    {
        var findings = await CheckAsync(
            """
            CREATE PROCEDURE dbo.GetOrderSummary AS
            BEGIN
                SELECT OrderId FROM dbo.OrderSummaryStaging;
            END;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.GetOrderSummary", finding.ModuleQualifiedName);
        Assert.Equal("stored procedure", finding.ModuleTypeDescription);
        Assert.Equal("OrderSummaryStaging", finding.ReferencedEntityName);
    }

    [Fact]
    public async Task ProcedureReferencingOnlyRealTables_ReportsNothing()
    {
        var findings = await CheckAsync(
            """
            CREATE TABLE dbo.CustomerAudit (AuditId INT NOT NULL, CustomerId INT NOT NULL);
            GO
            CREATE PROCEDURE dbo.GetCustomerAuditCount
                @CustomerId INT
            AS
            BEGIN
                SELECT COUNT(*) FROM dbo.CustomerAudit WHERE CustomerId = @CustomerId;
            END;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task ViewLeftBehindAfterItsBaseTableWasDropped_IsReportedAsAView()
    {
        // Unlike CREATE PROCEDURE, CREATE VIEW validates its referenced tables immediately - a
        // view can never be created against a table that never existed. The real way a view ends
        // up dangling is a base table dropped out from under it afterward, which SQL Server does
        // not retroactively invalidate the view for.
        var findings = await CheckAsync(
            """
            CREATE TABLE dbo.WidgetInventory (WidgetId INT NOT NULL);
            GO
            CREATE VIEW dbo.vw_ActiveWidgets AS
            SELECT WidgetId FROM dbo.WidgetInventory;
            GO
            DROP TABLE dbo.WidgetInventory;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.vw_ActiveWidgets", finding.ModuleQualifiedName);
        Assert.Equal("view", finding.ModuleTypeDescription);
        Assert.Equal("WidgetInventory", finding.ReferencedEntityName);
    }

    [Fact]
    public async Task TriggerReferencingInsertedPseudoTable_IsNotReported()
    {
        // "inserted"/"deleted" are trigger pseudo-tables - always unresolvable to a real object
        // ID by design, never a real broken reference.
        var findings = await CheckAsync(
            """
            CREATE TABLE dbo.WidgetInventory (WidgetId INT NOT NULL);
            GO
            CREATE TRIGGER dbo.trg_WidgetInventory_Insert ON dbo.WidgetInventory AFTER INSERT AS
            BEGIN
                SELECT WidgetId FROM inserted;
            END;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task ProcedureCallingAnUnqualifiedProcedureName_IsCallerDependentAndNotReported()
    {
        // EXEC SomeProc with no schema at all resolves at run time relative to the caller's own
        // context (is_caller_dependent = 1, oracle-confirmed) - not a missing-object claim. A
        // schema-qualified EXEC dbo.SomeProc is NOT caller-dependent and IS a genuine missing-
        // object claim (SQL Server's own deferred-name-resolution warning says exactly this at
        // CREATE time) - deliberately not tested as a "not reported" case here.
        var findings = await CheckAsync(
            """
            CREATE PROCEDURE dbo.RunReport AS
            BEGIN
                EXEC SomeUnqualifiedProcThatMayNotExist;
            END;
            """);

        Assert.Empty(findings);
    }

    private static async Task<IReadOnlyList<DanglingObjectReferenceFinding>> CheckAsync(string sql)
    {
        var databaseName = $"SilentScanTest_{Guid.NewGuid():N}";
        var provisioner = new DatabaseProvisioner(Options);
        await provisioner.CreateFreshAsync(databaseName);
        try
        {
            await new ScriptDeployer(Options).DeployAsync(sql, databaseName);
            return await new DanglingObjectReferenceChecker(Options.BuildConnectionString(databaseName)).CheckAsync();
        }
        finally
        {
            await provisioner.DropIfExistsAsync(databaseName);
        }
    }
}
