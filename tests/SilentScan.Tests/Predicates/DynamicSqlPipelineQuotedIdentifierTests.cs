using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;

namespace SilentScan.Tests.Predicates;

public sealed class DynamicSqlPipelineQuotedIdentifierTests
{
    private const string SchemaSql = "CREATE TABLE dbo.T (Col INT NOT NULL);";

    private const string AppSql =
        "CREATE PROCEDURE dbo.usp_Outer AS\n" +
        "BEGIN\n" +
        "    EXEC('EXEC(\"SELECT 1\")');\n" +
        "END\n";

    private static (DatabaseCatalog Catalog, LineageCatalog Lineage) BuildCatalog()
    {
        var schema = SqlScriptParser.ParseText("schema.sql", SchemaSql);
        Assert.False(schema.HasErrors, string.Join("; ", schema.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([schema]);
        var lineage = LineageResolver.Resolve(catalog, [schema]);
        return (catalog, lineage);
    }

    private static DynamicSqlScript ExtractOuterScript()
    {
        var parseResult = SqlScriptParser.ParseText("app.sql", AppSql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var extraction = DynamicSqlScannerV2.Scan(parseResult);
        return Assert.Single(extraction.AnalyzableScripts);
    }

    [Fact]
    public void Analyze_ModuleUnknownQuotedIdentifierState_FailsToParseInnerDoubleQuotedExec()
    {
        var (catalog, lineage) = BuildCatalog();
        var script = ExtractOuterScript();

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.InnerParseFailed, finding.Outcome);
    }

    [Fact]
    public void Analyze_ModuleCompiledUnderQuotedIdentifierOff_ParsesInnerDoubleQuotedExecCleanly()
    {
        var (catalog, lineage) = BuildCatalog();
        catalog.AddModuleUsesQuotedIdentifier("dbo.usp_Outer", usesQuotedIdentifier: false);
        var script = ExtractOuterScript();

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        Assert.DoesNotContain(result.Findings, f => f.Outcome == DynamicSqlOutcome.InnerParseFailed);
    }
}
