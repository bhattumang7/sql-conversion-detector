using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Tests.Support;
using SilentScan.Verify.Catalog;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class CatalogBuilderCrossModuleDropTableOracleTests : OracleTestFixture
{
    private const string GlobalTempName = "##CatalogBuilderCrossModuleDropRepro";

    protected override string DatabaseNameSeed => nameof(CatalogBuilderCrossModuleDropTableOracleTests);

    protected override string Ddl => $$"""
        CREATE PROCEDURE dbo.AAA_DropsGlobalTemp AS
        BEGIN
            DROP TABLE {{GlobalTempName}};
        END
        GO
        CREATE PROCEDURE dbo.ZZZ_CreatesGlobalTemp AS
        BEGIN
            CREATE TABLE {{GlobalTempName}} (Id INT NOT NULL);
        END
        """;

    private async Task<DatabaseCatalog> BuildExtrasCatalogAsync()
    {
        var connectionString = Options.BuildConnectionString(DatabaseName);
        var liveCatalog = await new LiveCatalogReader(connectionString).ReadAsync();
        var moduleResult = await new LiveModuleReader(connectionString).ReadAsync();

        var parseResults = moduleResult.Modules
            .Select(m => SqlScriptParser.ParseText(m.QualifiedName, m.Definition, m.UsesQuotedIdentifier, liveCatalog.CompatibilityLevel))
            .ToList();

        var knownPermanentTables = liveCatalog.Tables.Where(t => t.Kind == CatalogTableKind.Table).ToList();

        return CatalogBuilder.Build(
            parseResults, liveCatalog.DefaultCollation?.Name, liveCatalog.TempdbCollation?.Name, liveCatalog.IsAnsiNullDefaultOn,
            knownTables: knownPermanentTables);
    }

    [Fact]
    public async Task RealServer_ModuleReadOrderPutsCreateAfterDrop_ConfirmingTheReproShape()
    {
        var moduleResult = await new LiveModuleReader(Options.BuildConnectionString(DatabaseName)).ReadAsync();

        var names = moduleResult.Modules.Select(m => m.ObjectName).ToList();
        Assert.Equal(["AAA_DropsGlobalTemp", "ZZZ_CreatesGlobalTemp"], names);
    }

    [Fact]
    public async Task DropTable_TargetingGlobalTempCreatedInALaterModule_IsNotReportedAsUnresolved()
    {
        var catalog = await BuildExtrasCatalogAsync();

        Assert.DoesNotContain(catalog.Skipped.Entries, e => e.ConstructKind == "DROP TABLE" && e.Reason.Contains(GlobalTempName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DropTable_TargetingGlobalTempCreatedInALaterModule_ActuallyRemovesItFromTheCatalog()
    {
        var catalog = await BuildExtrasCatalogAsync();

        Assert.Null(catalog.Find(GlobalTempName));
    }
}
