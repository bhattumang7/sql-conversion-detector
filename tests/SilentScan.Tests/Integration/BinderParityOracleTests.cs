using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

/// <summary>
/// The regression guard docs/detection-checklist.md's Phase 1.5 "one binder" item names: for a
/// statement with a resolvable predicate, assert SilentScan's own statically-resolved
/// <see cref="ColumnProvenance.BaseColumn"/> agrees with the real execution plan's own
/// <see cref="ResolvedColumnReference"/> for the same binding - the engine's own algebrizer
/// answer, already-supported, no reverse engineering. Extends <see
/// cref="LineageOracleIntegrationTests"/>'s "result-set shape" lineage-parity pattern to
/// predicate/column binding specifically.
///
/// Deliberately targets the exact bug class the seven scanner migrations preceding this test
/// fixed: a CTE named identically to a real base table, which a naive resolver could silently
/// bind against the wrong (unrelated, same-named) real table instead of the CTE's own real
/// underlying column. dbo.vw_CteShadowsRealTable (fixtures/oracle/lineage_probe_schema.sql) is
/// exactly that shape.
///
/// Requires the docker-compose SQL Server (docs/local-dev.md) to be running; there is no mock or
/// skip path here, matching LineageOracleIntegrationTests' own precedent.
/// </summary>
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

    /// <summary>
    /// The static side: parses the identical DDL text deployed live in <see cref="InitializeAsync"/>
    /// (file mode, no live connection - CatalogBuilder resolves against tables only per CLAUDE.md's
    /// pass-ordering rule) and resolves dbo.vw_CteShadowsRealTable through LineageResolver, the
    /// same public pass SilentScan uses for every real view in a scan.
    /// </summary>
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
        // No live connection needed for this half - proves the fix on its own before the parity
        // assertion below cross-checks it against the real engine.
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

        // The real engine's own algebrizer, reading straight through the view AND the CTE, must
        // land on the same real table/column SilentScan's static resolution did - this is the
        // actual parity assertion, not two independent correctness checks.
        Assert.Contains(planReferences, r =>
            string.Equals(r.Table, "Orders", StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Column, "OrderCode", StringComparison.OrdinalIgnoreCase)
            && string.Equals("dbo", r.Schema, StringComparison.OrdinalIgnoreCase)
            && string.Equals($"{r.Schema}.{r.Table}", staticBaseColumn.TableQualifiedName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Column, staticBaseColumn.ColumnName, StringComparison.OrdinalIgnoreCase));
    }
}
