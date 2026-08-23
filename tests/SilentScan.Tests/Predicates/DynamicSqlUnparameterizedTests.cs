using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;

namespace SilentScan.Tests.Predicates;

public sealed class DynamicSqlUnparameterizedTests
{
    private const string SchemaSql = "CREATE TABLE dbo.T (Code VARCHAR(20) NOT NULL, Name VARCHAR(50) NOT NULL);";

    private static (DatabaseCatalog Catalog, LineageCatalog Lineage) BuildCatalog()
    {
        var schema = SqlScriptParser.ParseText("schema.sql", SchemaSql);
        Assert.False(schema.HasErrors, string.Join("; ", schema.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([schema]);
        var lineage = LineageResolver.Resolve(catalog, [schema]);
        return (catalog, lineage);
    }

    private static DynamicSqlPipelineResult Analyze(string appSql)
    {
        var (catalog, lineage) = BuildCatalog();
        var parseResult = SqlScriptParser.ParseText("app.sql", appSql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var extraction = DynamicSqlScannerV2.Scan(parseResult);
        var script = Assert.Single(extraction.AnalyzableScripts);
        return DynamicSqlPipeline.Analyze([script], catalog, lineage);
    }

    [Fact]
    public void ExecStringConcatenatesLiteralValue_FiresBothKinds()
    {
        var appSql =
            "CREATE PROCEDURE dbo.usp_Find AS\n" +
            "BEGIN\n" +
            "    DECLARE @Code VARCHAR(20) = 'ABC'\n" +
            "    EXEC('SELECT * FROM dbo.T WHERE Code = ''' + @Code + '''')\n" +
            "END\n";

        var result = Analyze(appSql);

        Assert.Equal(2, result.UnparameterizedFindings.Count);
        Assert.Contains(result.UnparameterizedFindings, f => f.Kind == UnparameterizedDynamicSqlFindingKind.ConcatenatedValueInConstantSql);
        Assert.Contains(result.UnparameterizedFindings, f => f.Kind == UnparameterizedDynamicSqlFindingKind.ExecStringConcatenatesParameterizableValue);
        Assert.All(result.UnparameterizedFindings, f =>
        {
            Assert.Equal("app.sql", f.SourcePath);
            Assert.Equal(4, f.Line);        });
    }

    [Fact]
    public void SpExecuteSqlConcatenatesLiteralValueIntoTextInsteadOfUsingParams_FiresGeneralKindOnly()
    {
        var appSql =
            "CREATE PROCEDURE dbo.usp_Find AS\n" +
            "BEGIN\n" +
            "    DECLARE @Code VARCHAR(20) = 'ABC'\n" +
            "    DECLARE @sql NVARCHAR(MAX) = N'SELECT * FROM dbo.T WHERE Code = ''' + @Code + ''''\n" +
            "    EXEC sp_executesql @sql\n" +
            "END\n";

        var result = Analyze(appSql);

        var finding = Assert.Single(result.UnparameterizedFindings);
        Assert.Equal(UnparameterizedDynamicSqlFindingKind.ConcatenatedValueInConstantSql, finding.Kind);
    }

    [Fact]
    public void ExecOfWholeSingleLiteral_NoConcatenation_NeverFires()
    {
        var appSql = "EXEC('SELECT * FROM dbo.T WHERE Code = ''ABC''');";

        var result = Analyze(appSql);

        Assert.Empty(result.UnparameterizedFindings);
    }

    [Fact]
    public void ExecStringConcatenatesIdentifierNotValue_NeverFires()
    {
        var appSql =
            "CREATE PROCEDURE dbo.usp_Find AS\n" +
            "BEGIN\n" +
            "    DECLARE @TableName VARCHAR(20) = 'T'\n" +
            "    EXEC('SELECT * FROM dbo.' + @TableName)\n" +
            "END\n";

        var result = Analyze(appSql);

        Assert.Empty(result.UnparameterizedFindings);
    }
}
