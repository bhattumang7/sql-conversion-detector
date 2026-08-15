using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// The MSTVF-as-fence stream folds through provably-constant dynamic SQL exactly like the
/// sargability/typed streams already do (CLAUDE.md's dynamic SQL policy: "run back through the
/// normal pipeline"), remapped to the fence reference's true source line rather than the EXEC
/// call site. Docker-free, matching <see cref="DynamicSqlFixtureEndToEndTests"/>'s own reasoning:
/// the remap machinery itself is provenance-only and already oracle-covered for other streams.
/// </summary>
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
        // The reference sits on the second line of the folded literal (line 5 of app.sql), not
        // the EXEC call site itself (line 4) - the whole reason this stream needs its own remap
        // wiring rather than reusing another stream's.
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

        // The pre-existing 4-arg overload (no fence map) must keep compiling and behaving
        // exactly as it did before this stream existed - every caller that doesn't care about
        // TVF fences (most unit tests) should never have to learn about this parameter.
        var pipeline = DynamicSqlPipeline.Analyze(extraction.AnalyzableScripts, catalog, lineage);
        Assert.Empty(pipeline.TvfFenceFindings);
    }
}
