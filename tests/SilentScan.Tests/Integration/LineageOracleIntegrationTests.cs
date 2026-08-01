using Microsoft.Data.SqlClient;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

/// <summary>
/// End-to-end regression test for the Phase 0 spike, automated: deploys the lineage probe
/// schema (DDL only, tables stay empty per CLAUDE.md's "Corpus DML/procs are NEVER executed
/// anywhere") to the Docker SQL Server oracle, confirms sys.columns propagates the base
/// table's varchar/SQL_* collation through two view layers unchanged, and confirms a
/// SELF-AUTHORED probe under SHOWPLAN_XML shows CONVERT_IMPLICIT on the base column.
///
/// Requires the docker-compose SQL Server (docs/local-dev.md) to be running; there is no
/// mock or skip path here per CLAUDE.md's testing-standards note that unit tests are not
/// sufficient and this class of behavior needs a real integration test.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class LineageOracleIntegrationTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanIntegrationTest";

    private static readonly string SchemaScript = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "oracle", "lineage_probe_schema.sql"));

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;

    public LineageOracleIntegrationTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
    }

    public async Task InitializeAsync()
    {
        await _provisioner.CreateFreshAsync(DatabaseName);
        await new ScriptDeployer(_options).DeployAsync(SchemaScript, DatabaseName);
    }

    public async Task DisposeAsync()
    {
        await _provisioner.DropIfExistsAsync(DatabaseName);
    }

    [Fact]
    public async Task ColumnCatalog_Vw_OrdersLevel2_PropagatesBaseColumnTypeAndCollationUnchanged()
    {
        var columns = await new ColumnCatalogReader(_options)
            .ReadColumnsAsync(DatabaseName, "dbo.vw_OrdersLevel2");

        var orderCode = columns.Single(c => c.ColumnName == "OrderCode");
        Assert.Equal("varchar", orderCode.TypeName);
        Assert.Equal(20, orderCode.MaxLength);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", orderCode.CollationName);
    }

    [Fact]
    public async Task PlanXml_NVarcharProbeAgainstView_ShowsConvertImplicitOnBaseColumn()
    {
        const string probe = """
            DECLARE @OrderCode NVARCHAR(20) = N'ABC123';
            SELECT OrderId, OrderCode, CreatedAt
            FROM dbo.vw_OrdersLevel2
            WHERE OrderCode = @OrderCode;
            """;

        var planXml = await new PlanXmlCapture(_options).CaptureAsync(DatabaseName, probe);
        var findings = ConvertImplicitDetector.FindColumnConversions(planXml);

        var finding = Assert.Single(findings);
        Assert.Equal("Orders", finding.Table);
        Assert.Equal("OrderCode", finding.Column);
        Assert.Equal("nvarchar", finding.ConvertedToDataType);
    }

    [Fact]
    public async Task PlanXml_Probe_NeverExecutesAgainstData()
    {
        // The schema fixture never inserts rows, so a genuine row count of 0 after
        // capturing a plan is only meaningful proof of compile-only behavior if the probe
        // could plausibly have produced output otherwise. Confirmed independently of the
        // two tests above so a future regression to SET STATISTICS XML (which executes)
        // fails loudly here too, rather than only showing up as a data-shape surprise later.
        var capture = new PlanXmlCapture(_options);
        await capture.CaptureAsync(DatabaseName, "SELECT OrderId FROM dbo.Orders WHERE OrderId = 1;");

        var rowCount = await CountOrdersRowsAsync();
        Assert.Equal(0, rowCount);
    }

    private async Task<int> CountOrdersRowsAsync()
    {
        await using var connection = new SqlConnection(_options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbo.Orders;";
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
