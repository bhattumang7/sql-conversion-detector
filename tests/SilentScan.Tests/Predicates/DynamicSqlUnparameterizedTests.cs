using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "Dynamic SQL quality", items 1+2 -
/// <see cref="UnparameterizedDynamicSqlFinding"/>. File-mode (no oracle needed): the underlying
/// claim this finding makes about the query PLAN CACHE is confirmed once, directly against the
/// Docker oracle, in <see cref="DynamicSqlUnparameterizedOracleTests"/> - these tests only need to
/// prove the AST/segment-map detection itself is correct.
/// </summary>
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
            Assert.Equal(4, f.Line); // the EXEC( call site itself
        });
    }

    [Fact]
    public void SpExecuteSqlConcatenatesLiteralValueIntoTextInsteadOfUsingParams_FiresGeneralKindOnly()
    {
        // The exact real-world antipattern this finding targets: the author already reached for
        // sp_executesql, but still concatenated the value into the SQL TEXT rather than using its
        // own @params mechanism - ConcatenatedValueInConstantSql still fires (the plan-cache
        // pollution is real regardless), but ExecStringConcatenatesParameterizableValue must NOT -
        // that finding's own claim ("switch to sp_executesql") makes no sense for a call site that
        // already IS sp_executesql.
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
        // The value/identifier distinction this stream exists to make: concatenating a proven-
        // constant TABLE NAME (a real, often-unavoidable dynamic-object pattern) is a completely
        // different phenomenon from concatenating a comparison VALUE - never flagged here.
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
