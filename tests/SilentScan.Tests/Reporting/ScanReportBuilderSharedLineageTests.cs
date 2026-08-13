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

/// <summary>
/// A live scan must resolve lineage itself to run the parity gate before the report is built, and
/// <see cref="ScanReportBuilder.BuildFromParseResults"/> used to resolve its own a second time -
/// one of the two most expensive passes in the run, paid for twice. It now accepts the caller's
/// already-resolved catalog.
///
/// The whole optimization rests on one claim: lineage is a pure function of (catalog,
/// parseResults), so reusing the caller's instance cannot change a single finding. These pin that
/// equivalence on real deployed schemas rather than trusting the argument - a report that differs
/// between the two paths means every live-mode finding is now built on a different lineage than
/// the parity gate vetted.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class ScanReportBuilderSharedLineageTests
{
    private static readonly SqlServerOptions Options = SqlServerOptions.LocalDocker;

    [Fact]
    public async Task LayeredViewsWithAConversion_ProduceIdenticalReportsEitherWay()
    {
        // Two view layers between the predicate and the base column, so the findings under
        // comparison actually depend on lineage resolution rather than being direct table hits.
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

        // Guard the whole comparison: a scan finding nothing would make every assertion below
        // pass vacuously, and this fixture exists precisely because it DOES convert the column.
        Assert.NotEmpty(withOwn.TypedFindings);
        AssertReportsEquivalent(withOwn, withShared);
    }

    [Fact]
    public async Task CastInsideAViewLayer_ProducesIdenticalReportsEitherWay()
    {
        // A CAST introduced mid-layer is the case where provenance depth/origin - the parts of a
        // finding most sensitive to which lineage instance produced it - actually carry content.
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
        // Tier-1 and the dynamic-SQL pipeline both take lineage too, so the equivalence has to
        // hold for those finding streams and not just the typed one.
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

    /// <summary>
    /// Compares the two reports through the same JSON shape <c>scan-db --format json</c> emits.
    /// Record equality is NOT usable here: several finding records (e.g.
    /// <c>ExpressionDerivedFinding</c>) carry <c>List&lt;T&gt;</c> members, and a positional
    /// record compares those by REFERENCE - so two structurally identical findings built by two
    /// separate passes always compare unequal, and the assertion would fail on reports that are
    /// in fact byte-identical to every consumer. Serializing compares the values that actually
    /// reach a user, all the way down the transformation chains and base-column lists.
    /// </summary>
    private static void AssertReportsEquivalent(ScanReport withOwn, ScanReport withShared) =>
        Assert.Equal(
            JsonSerializer.Serialize(withOwn, ComparisonOptions),
            JsonSerializer.Serialize(withShared, ComparisonOptions));

    /// <summary>
    /// Deploys <paramref name="sql"/> once and builds the report twice off the SAME catalog and
    /// parse results - once letting the builder resolve its own lineage, once handing it a
    /// separately-resolved instance, exactly as <c>LiveScanRunner</c> now does.
    /// </summary>
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
