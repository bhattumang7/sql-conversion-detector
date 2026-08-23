using SilentScan.Core.Predicates;
using SilentScan.Live.Catalog;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Integration;

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
