using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;

namespace SilentScan.Tests.Predicates;

public sealed class DynamicSqlTvfFenceTests
{
    private const string SchemaSql = """
        CREATE FUNCTION dbo.fn_Fence()
        RETURNS @T TABLE (Id INT)
        AS
        BEGIN
            INSERT INTO @T (Id) SELECT 1;
            RETURN;
        END;
        GO
        CREATE FUNCTION dbo.itvf_Wrapper()
        RETURNS TABLE
        AS
        RETURN (SELECT Id FROM dbo.fn_Fence());
        """;

    private static (DatabaseCatalog Catalog, LineageCatalog Lineage, IReadOnlyDictionary<string, TvfFenceOrigin> FenceMap) BuildContext()
    {
        var schema = SqlScriptParser.ParseText("schema.sql", SchemaSql);
        Assert.False(schema.HasErrors, string.Join("; ", schema.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([schema]);
        var lineage = LineageResolver.Resolve(catalog, [schema]);
        var (views, _) = ViewDefinitionExtractor.Extract([schema], catalog.DefaultCollation, catalog.TypeAliases);
        var fenceMap = TvfFenceMap.Build(views, catalog);
        return (catalog, lineage, fenceMap);
    }

    [Fact]
    public void DirectFenceReferenceInsideExec_RemapsToTrueSourceLine()
    {
        var (catalog, lineage, fenceMap) = BuildContext();

        var appSql =
            "CREATE PROCEDURE dbo.usp_Find\n" +
            "AS\n" +
            "BEGIN\n" +
            "    EXEC('SELECT Id\n" +
            "FROM dbo.fn_Fence()');\n" +
            "END\n";
        var parseResult = SqlScriptParser.ParseText("app.sql", appSql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var extraction = DynamicSqlScannerV2.Scan(parseResult, callGraph: new ProcCallGraph([]));
        Assert.Empty(extraction.Findings);
        Assert.NotEmpty(extraction.AnalyzableScripts);

        var pipeline = DynamicSqlPipeline.Analyze(extraction.AnalyzableScripts, catalog, lineage, fenceMap);

        var finding = Assert.Single(pipeline.TvfFenceFindings);
        Assert.Equal(TvfFenceFindingKind.Standalone, finding.Kind);
        Assert.Equal("dbo.fn_Fence", finding.FunctionQualifiedName);
        Assert.Equal("app.sql", finding.SourcePath);

        Assert.Equal(5, finding.Line);
        Assert.NotNull(finding.DynamicSqlCallSite);
        Assert.Equal(4, finding.DynamicSqlCallSite!.Value.Line);
    }

    [Fact]
    public void NestedFenceThroughInlineTvf_RemapsCorrectlyInsideDynamicSql()
    {
        var (catalog, lineage, fenceMap) = BuildContext();

        var appSql =
            "CREATE PROCEDURE dbo.usp_Find\n" +
            "AS\n" +
            "BEGIN\n" +
            "    EXEC('SELECT Id FROM dbo.itvf_Wrapper()');\n" +
            "END\n";
        var parseResult = SqlScriptParser.ParseText("app.sql", appSql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var extraction = DynamicSqlScannerV2.Scan(parseResult, callGraph: new ProcCallGraph([]));
        Assert.Empty(extraction.Findings);

        var pipeline = DynamicSqlPipeline.Analyze(extraction.AnalyzableScripts, catalog, lineage, fenceMap);

        var finding = Assert.Single(pipeline.TvfFenceFindings);
        Assert.Equal(TvfFenceFindingKind.NestedUnderViewOrTvf, finding.Kind);
        Assert.Equal("dbo.itvf_Wrapper", finding.ReferencedObjectQualifiedName);
        Assert.Equal("dbo.fn_Fence", finding.FunctionQualifiedName);
        Assert.Equal(1, finding.Depth);
    }

    [Fact]
    public void NoFenceMapPassed_UsesDefaultEmptyMap_NeverThrows()
    {
        var (catalog, lineage, _) = BuildContext();

        var appSql = "SELECT 1;";
        var parseResult = SqlScriptParser.ParseText("app.sql", appSql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var extraction = DynamicSqlScannerV2.Scan(parseResult, callGraph: new ProcCallGraph([]));

        var pipeline = DynamicSqlPipeline.Analyze(extraction.AnalyzableScripts, catalog, lineage);
        Assert.Empty(pipeline.TvfFenceFindings);
    }
}
