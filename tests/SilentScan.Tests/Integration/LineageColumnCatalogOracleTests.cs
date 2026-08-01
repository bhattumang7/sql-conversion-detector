using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

/// <summary>
/// The Phase 2 exit criterion (plan.md): "lineage inference is oracle-clean on all fixtures,
/// including a 5-deep view chain and a mixed-collation UNION." Deploys the fixture (DDL
/// only, tables stay empty) to the Docker SQL Server oracle and diffs our static
/// LineageResolver output against sys.columns - the free ground-truth oracle CLAUDE.md's
/// Verify workflow calls for ("ANY mismatch is a P0 bug").
/// </summary>
[Trait("Category", "Oracle")]
public sealed class LineageColumnCatalogOracleTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanLineageOracleTest";

    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "fixtures", "lineage", "five_deep_chain_and_union.sql");

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;
    private DatabaseCatalog _catalog = null!;
    private LineageCatalog _lineage = null!;

    public LineageColumnCatalogOracleTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
    }

    public async Task InitializeAsync()
    {
        var script = await File.ReadAllTextAsync(FixturePath);

        await _provisioner.CreateFreshAsync(DatabaseName);
        await new ScriptDeployer(_options).DeployAsync(script, DatabaseName);

        var parseResult = SqlScriptParser.ParseFile(FixturePath);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));
        _catalog = CatalogBuilder.Build([parseResult]);
        _lineage = LineageResolver.Resolve(_catalog, [parseResult]);
    }

    public async Task DisposeAsync()
    {
        await _provisioner.DropIfExistsAsync(DatabaseName);
    }

    [Fact]
    public async Task FiveDeepViewChain_InferredBaseColumnType_MatchesSysColumns()
    {
        var oracleColumns = await new ColumnCatalogReader(_options).ReadColumnsAsync(DatabaseName, "dbo.vw_L5");
        var oracleOrderCode = oracleColumns.Single(c => c.ColumnName == "OrderCode");

        var view = _lineage.Find("dbo.vw_L5")!;
        var inferred = Assert.IsType<ColumnProvenance.BaseColumn>(view.FindColumn("OrderCode")!.Provenance);

        Assert.Equal("dbo.Orders", inferred.TableQualifiedName);
        Assert.Equal(oracleOrderCode.TypeName, inferred.Type!.Category.ToString(), ignoreCase: true);
        Assert.Equal(oracleOrderCode.MaxLength, inferred.Type.Length);
        Assert.Equal(oracleOrderCode.CollationName, inferred.Type.Collation!.Name);
    }

    [Fact]
    public async Task MixedBranchUnion_ServerResolvedType_MatchesTheWiderRecordedBranch()
    {
        var oracleColumns = await new ColumnCatalogReader(_options).ReadColumnsAsync(DatabaseName, "dbo.vw_AllOrders");
        var oracleOrderCode = oracleColumns.Single(c => c.ColumnName == "OrderCode");
        var oracleOrderId = oracleColumns.Single(c => c.ColumnName == "OrderId");

        var view = _lineage.Find("dbo.vw_AllOrders")!;
        var orderCodeUnion = Assert.IsType<ColumnProvenance.Union>(view.FindColumn("OrderCode")!.Provenance);
        var orderIdUnion = Assert.IsType<ColumnProvenance.Union>(view.FindColumn("OrderId")!.Provenance);

        // CLAUDE.md: "record ALL branch types" - both must be present, distinct, and each a
        // real base-column reference (not collapsed into one merged guess).
        Assert.Equal(2, orderCodeUnion.Branches.Count);
        var codeBranch1 = Assert.IsType<ColumnProvenance.BaseColumn>(orderCodeUnion.Branches[0]);
        var codeBranch2 = Assert.IsType<ColumnProvenance.BaseColumn>(orderCodeUnion.Branches[1]);
        Assert.Equal(20, codeBranch1.Type!.Length);
        Assert.Equal(30, codeBranch2.Type!.Length);

        // The server itself resolves the UNION to the wider of the two recorded branch types.
        Assert.Equal("varchar", oracleOrderCode.TypeName);
        Assert.Equal(Math.Max(codeBranch1.Type.Length!.Value, codeBranch2.Type.Length!.Value), (int)oracleOrderCode.MaxLength);

        var idBranch1 = Assert.IsType<ColumnProvenance.BaseColumn>(orderIdUnion.Branches[0]);
        var idBranch2 = Assert.IsType<ColumnProvenance.BaseColumn>(orderIdUnion.Branches[1]);
        Assert.Equal(SqlTypeCategory.Int, idBranch1.Type!.Category);
        Assert.Equal(SqlTypeCategory.BigInt, idBranch2.Type!.Category);

        // The server resolves to the higher-precedence branch type (bigint > int).
        Assert.Equal("bigint", oracleOrderId.TypeName);
    }
}
