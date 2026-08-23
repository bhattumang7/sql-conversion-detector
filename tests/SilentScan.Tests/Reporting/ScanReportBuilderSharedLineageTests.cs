using System.Text.Json;
using System.Text.Json.Serialization;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Tests.Support;
using SilentScan.Verify;
using SilentScan.Verify.Catalog;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Reporting;

[Trait("Category", "Oracle")]
public sealed class ScanReportBuilderSharedLineageTests
{
    private static readonly SqlServerOptions Options = SqlServerOptions.LocalDocker;

    [Fact]
    public async Task LayeredViewsWithAConversion_ProduceIdenticalReportsEitherWay()
    {
        var (withOwn, withShared) = await BuildBothWaysAsync(
            """
            CREATE TABLE dbo.Customers (Code VARCHAR(20) NOT NULL, Name NVARCHAR(100) NULL);
            GO
            CREATE INDEX IX_Customers_Code ON dbo.Customers (Code);
            GO
            CREATE VIEW dbo.vw_Customers AS SELECT Code, Name FROM dbo.Customers;
            GO
            CREATE VIEW dbo.vw_CustomersOuter AS SELECT Code, Name FROM dbo.vw_Customers;
            GO
            CREATE PROCEDURE dbo.usp_FindCustomer @Code NVARCHAR(20) AS
            BEGIN
                SELECT Name FROM dbo.vw_CustomersOuter WHERE Code = @Code;
            END
            """);

        Assert.NotEmpty(withOwn.TypedFindings);
        AssertReportsEquivalent(withOwn, withShared);
    }

    [Fact]
    public async Task CastInsideAViewLayer_ProducesIdenticalReportsEitherWay()
    {
        var (withOwn, withShared) = await BuildBothWaysAsync(
            """
            CREATE TABLE dbo.Orders (OrderRef INT NOT NULL);
            GO
            CREATE INDEX IX_Orders_OrderRef ON dbo.Orders (OrderRef);
            GO
            CREATE VIEW dbo.vw_Orders AS SELECT CAST(OrderRef AS VARCHAR(20)) AS OrderRef FROM dbo.Orders;
            GO
            CREATE PROCEDURE dbo.usp_FindOrder @Ref NVARCHAR(20) AS
            BEGIN
                SELECT OrderRef FROM dbo.vw_Orders WHERE OrderRef = @Ref;
            END
            """);

        AssertReportsEquivalent(withOwn, withShared);
    }

    [Fact]
    public async Task SyntacticAndDynamicSqlStreams_AreIdenticalEitherWay()
    {
        var (withOwn, withShared) = await BuildBothWaysAsync(
            """
            CREATE TABLE dbo.Events (Name VARCHAR(50) NOT NULL, CreatedAt DATETIME NOT NULL);
            GO
            CREATE INDEX IX_Events_Name ON dbo.Events (Name);
            GO
            CREATE VIEW dbo.vw_Events AS SELECT Name, CreatedAt FROM dbo.Events;
            GO
            CREATE PROCEDURE dbo.usp_Events AS
            BEGIN
                SELECT Name FROM dbo.vw_Events WHERE UPPER(Name) = 'X';
                EXEC sp_executesql N'SELECT Name FROM dbo.vw_Events WHERE Name = N''y''';
            END
            """);

        Assert.NotEmpty(withOwn.Tier1Findings);
        AssertReportsEquivalent(withOwn, withShared);
    }

    private static readonly JsonSerializerOptions ComparisonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

private static void AssertReportsEquivalent(ScanReport withOwn, ScanReport withShared) =>
        Assert.Equal(
            JsonSerializer.Serialize(withOwn, ComparisonOptions),
            JsonSerializer.Serialize(withShared, ComparisonOptions));

private static async Task<(ScanReport WithOwnLineage, ScanReport WithSharedLineage)> BuildBothWaysAsync(string sql)
    {
        var databaseName = $"SilentScanTest_{Guid.NewGuid():N}";
        var provisioner = new DatabaseProvisioner(Options);
        await provisioner.CreateFreshAsync(databaseName);
        try
        {
            await new ScriptDeployer(Options).DeployAsync(sql, databaseName);
            var connectionString = Options.BuildConnectionString(databaseName);

            var catalog = await new LiveCatalogReader(connectionString).ReadAsync();
            var moduleResult = await new LiveModuleReader(connectionString).ReadAsync();
            var parseResults = moduleResult.Modules
                .Select(m => SqlScriptParser.ParseText(m.QualifiedName, m.Definition, m.UsesQuotedIdentifier))
                .ToList();

            catalog.MergeFileModeExtras(CatalogBuilder.Build(parseResults, catalog.DefaultCollation?.Name, catalog.TempdbCollation?.Name));

            var withOwnLineage = ScanReportBuilder.BuildFromParseResults(parseResults, catalog: catalog);
            var withSharedLineage = ScanReportBuilder.BuildFromParseResults(
                parseResults, catalog: catalog, resolvedLineage: LineageResolver.Resolve(catalog, parseResults));

            return (withOwnLineage, withSharedLineage);
        }
        finally
        {
            await provisioner.DropIfExistsAsync(databaseName);
        }
    }
}
