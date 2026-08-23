using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;

namespace SilentScan.Tests.Predicates;

public sealed class DynamicSqlScalarUdfTests
{
    private const string SchemaSql = """
        CREATE FUNCTION dbo.fn_IsActive(@x INT)
        RETURNS BIT
        AS
        BEGIN
            RETURN 1;
        END;
        GO
        CREATE TABLE dbo.T (Id INT NOT NULL);
        GO
        CREATE VIEW dbo.vw_Computed
        AS
        SELECT Id, dbo.fn_IsActive(Id) AS IsActive FROM dbo.T;
        """;

    private static (DatabaseCatalog Catalog, LineageCatalog Lineage, IReadOnlyDictionary<string, ScalarUdfOrigin> ScalarUdfMap) BuildContext()
    {
        var schema = SqlScriptParser.ParseText("schema.sql", SchemaSql);
        Assert.False(schema.HasErrors, string.Join("; ", schema.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([schema]);
        var lineage = LineageResolver.Resolve(catalog, [schema]);
        var (views, _) = ViewDefinitionExtractor.Extract([schema], catalog.DefaultCollation, catalog.TypeAliases);
        var scalarUdfMap = ScalarUdfMap.Build(views, catalog);
        return (catalog, lineage, scalarUdfMap);
    }

    [Fact]
    public void DirectPredicateCallInsideExec_RemapsToTrueSourceLine()
    {
        var (catalog, lineage, scalarUdfMap) = BuildContext();

        var appSql =
            "CREATE PROCEDURE dbo.usp_Find\n" +
            "AS\n" +
            "BEGIN\n" +
            "    EXEC('SELECT Id FROM dbo.T\n" +
            "WHERE dbo.fn_IsActive(Id) = 1');\n" +
            "END\n";
        var parseResult = SqlScriptParser.ParseText("app.sql", appSql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var extraction = DynamicSqlScannerV2.Scan(parseResult, callGraph: new ProcCallGraph([]));
        Assert.Empty(extraction.Findings);
        Assert.NotEmpty(extraction.AnalyzableScripts);

        var pipeline = DynamicSqlPipeline.Analyze(extraction.AnalyzableScripts, catalog, lineage, NoTvfFenceMap, scalarUdfMap);

        var finding = Assert.Single(pipeline.ScalarUdfFindings);
        Assert.Equal(ScalarUdfFindingKind.PredicateInvocation, finding.Kind);
        Assert.Equal("dbo.fn_IsActive", finding.FunctionQualifiedName);
        Assert.Equal("app.sql", finding.SourcePath);
        Assert.Equal(5, finding.Line);
        Assert.NotNull(finding.DynamicSqlCallSite);
        Assert.Equal(4, finding.DynamicSqlCallSite!.Value.Line);
    }

    [Fact]
    public void NestedUnderViewThroughDynamicSql_RemapsCorrectly()
    {
        var (catalog, lineage, scalarUdfMap) = BuildContext();

        var appSql =
            "CREATE PROCEDURE dbo.usp_Find\n" +
            "AS\n" +
            "BEGIN\n" +
            "    EXEC('SELECT Id FROM dbo.vw_Computed');\n" +
            "END\n";
        var parseResult = SqlScriptParser.ParseText("app.sql", appSql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var extraction = DynamicSqlScannerV2.Scan(parseResult, callGraph: new ProcCallGraph([]));
        Assert.Empty(extraction.Findings);

        var pipeline = DynamicSqlPipeline.Analyze(extraction.AnalyzableScripts, catalog, lineage, NoTvfFenceMap, scalarUdfMap);

        var finding = Assert.Single(pipeline.ScalarUdfFindings);
        Assert.Equal(ScalarUdfFindingKind.NestedUnderViewOrTvf, finding.Kind);
        Assert.Equal("dbo.vw_Computed", finding.ReferencedObjectQualifiedName);
        Assert.Equal("dbo.fn_IsActive", finding.FunctionQualifiedName);
        Assert.Equal(1, finding.Depth);
    }

    [Fact]
    public void NoScalarUdfMapPassed_UsesDefaultEmptyMap_NeverThrows()
    {
        var (catalog, lineage, _) = BuildContext();

        var appSql = "SELECT 1;";
        var parseResult = SqlScriptParser.ParseText("app.sql", appSql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var extraction = DynamicSqlScannerV2.Scan(parseResult, callGraph: new ProcCallGraph([]));

        var pipeline = DynamicSqlPipeline.Analyze(extraction.AnalyzableScripts, catalog, lineage);
        Assert.Empty(pipeline.ScalarUdfFindings);
    }

    private static readonly IReadOnlyDictionary<string, TvfFenceOrigin> NoTvfFenceMap = new Dictionary<string, TvfFenceOrigin>();
}
