using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class BinderParityOracleTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanBinderParityTest";

    private static readonly string SchemaScript = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "oracle", "lineage_probe_schema.sql"));

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;

    public BinderParityOracleTests()
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

private static ColumnProvenance.BaseColumn StaticallyResolvedOrderCodeProvenance()
    {
        var parseResult = SqlScriptParser.ParseText("lineage_probe_schema.sql", SchemaScript);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var view = lineage.Find("dbo.vw_CteShadowsRealTable");
        Assert.NotNull(view);

        var orderCode = view!.Columns.Single(c => c.Name == "OrderCode");
        return Assert.IsType<ColumnProvenance.BaseColumn>(orderCode.Provenance);
    }

    [Fact]
    public void Static_CteShadowsRealTable_ResolvesToRealUnderlyingColumn_NotTheCte()
    {
        var baseColumn = StaticallyResolvedOrderCodeProvenance();

        Assert.Equal("dbo.Orders", baseColumn.TableQualifiedName, ignoreCase: true);
        Assert.Equal("OrderCode", baseColumn.ColumnName, ignoreCase: true);
        Assert.Equal(0, baseColumn.Depth);
    }

    [Fact]
    public async Task Parity_StaticResolutionAgreesWithRealEnginePlanBinding()
    {
        const string probe = """
            SELECT OrderCode FROM dbo.vw_CteShadowsRealTable WHERE OrderCode = 'ABC123';
            """;

        var planXml = await new PlanXmlCapture(_options).CaptureAsync(DatabaseName, probe);
        var planReferences = BinderParityDetector.FindAllColumnReferences(planXml);

        var staticBaseColumn = StaticallyResolvedOrderCodeProvenance();

        Assert.Contains(planReferences, r =>
            string.Equals(r.Table, "Orders", StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Column, "OrderCode", StringComparison.OrdinalIgnoreCase)
            && string.Equals("dbo", r.Schema, StringComparison.OrdinalIgnoreCase)
            && string.Equals($"{r.Schema}.{r.Table}", staticBaseColumn.TableQualifiedName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Column, staticBaseColumn.ColumnName, StringComparison.OrdinalIgnoreCase));
    }
}
