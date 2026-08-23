using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class DynamicSqlPipelineTests : OracleTestFixture
{
    private const string SchemaSql =
        "CREATE TABLE dbo.T (Col VARCHAR(10) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, CreatedAt DATETIME NOT NULL); " +
        "CREATE INDEX IX_T_Col ON dbo.T(Col); \n" +
        "GO\n" +
        "CREATE VIEW dbo.vw_T AS SELECT CAST(Col AS INT) AS ColAsInt FROM dbo.T;\n" +
        "GO\n" +
        "CREATE VIEW dbo.vw_T_L1 AS SELECT Col FROM dbo.T;\n" +
        "GO\n" +
        "CREATE VIEW dbo.vw_T_L2 AS SELECT Col FROM dbo.vw_T_L1;";

    protected override string DatabaseNameSeed => nameof(DynamicSqlPipelineTests);

    protected override string Ddl => SchemaSql;

    private static (DatabaseCatalog Catalog, LineageCatalog Lineage) BuildCatalog()
    {
        var schema = SqlScriptParser.ParseText("schema.sql", SchemaSql);
        Assert.False(schema.HasErrors, string.Join("; ", schema.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([schema]);
        var lineage = LineageResolver.Resolve(catalog, [schema]);
        return (catalog, lineage);
    }

    [Fact]
    public async Task Analyze_MultiLineConcatenatedLiteral_RemapsFindingToSecondSourceLine_OracleConfirmed()
    {
        var (catalog, lineage) = BuildCatalog();

        var appSql =
            "CREATE PROCEDURE dbo.usp_Find\n" +
            "AS\n" +
            "BEGIN\n" +
            "    EXEC('SELECT Col FROM dbo.T\n" +
            "WHERE Col = N''x''');\n" +
            "END\n";
        var parseResult = SqlScriptParser.ParseText("app.sql", appSql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var extraction = DynamicSqlScannerV2.Scan(parseResult);
        Assert.Empty(extraction.Findings);
        var script = Assert.Single(extraction.AnalyzableScripts);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var dynamicFinding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.AnalyzedLiteral, dynamicFinding.Outcome);
        Assert.Equal(4, dynamicFinding.Line);

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.ScanForced, typedFinding.Verdict);
        Assert.Equal("app.sql", typedFinding.SourcePath);
        Assert.Equal(5, typedFinding.Line);
        Assert.NotNull(typedFinding.DynamicSqlCallSite);
        Assert.Equal(4, typedFinding.DynamicSqlCallSite!.Value.Line);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [typedFinding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task Analyze_ExecOfLiteralPredicateWithOptionalFilterFragmentAppended_PartiallyAnalyzesAndRemapsTheKnownPredicate_OracleConfirmed()
    {

        var (catalog, lineage) = BuildCatalog();

        var appSql =
            "CREATE PROCEDURE dbo.usp_Find @Extra NVARCHAR(100)\n" +
            "AS\n" +
            "BEGIN\n" +
            "    DECLARE @sql NVARCHAR(MAX) = 'SELECT Col FROM dbo.T\n" +
            "WHERE Col = N''x''' + @Extra\n" +
            "    EXEC(@sql)\n" +
            "END\n";
        var parseResult = SqlScriptParser.ParseText("app.sql", appSql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var extraction = DynamicSqlScannerV2.Scan(parseResult, callGraph: new ProcCallGraph([]));
        Assert.Empty(extraction.Findings);
        var script = Assert.Single(extraction.AnalyzableScripts);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var dynamicFinding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.PartiallyAnalyzed, dynamicFinding.Outcome);
        Assert.Equal("optional-fragment-elided", dynamicFinding.Reason);
        Assert.Equal(6, dynamicFinding.Line);

        var typedFinding2 = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.ScanForced, typedFinding2.Verdict);
        Assert.Equal("app.sql", typedFinding2.SourcePath);
        Assert.Equal(5, typedFinding2.Line);
        Assert.NotNull(typedFinding2.DynamicSqlCallSite);
        Assert.Equal(6, typedFinding2.DynamicSqlCallSite!.Value.Line);

        var results2 = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [typedFinding2]);
        PipelineOracleVerification.AssertAllConfirmed(results2);
    }

    [Fact]
    public async Task Analyze_ExecOfVariableDivergingAcrossIfElseIfBranches_BothBranchesScanForced_OracleConfirmed()
    {

        var (catalog, lineage) = BuildCatalog();

        var appSql = """
            CREATE PROCEDURE dbo.usp_Find @mode INT AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX)
                EXEC dbo.usp_Unknown @sql OUTPUT

                IF @mode = 1
                    SET @sql = N'SELECT Col FROM dbo.T WHERE Col = N''x'''
                ELSE IF @mode = 2
                    SET @sql = N'SELECT Col FROM dbo.T WHERE Col = N''y'''

                EXEC(@sql)
            END
            """;
        var parseResult = SqlScriptParser.ParseText("app.sql", appSql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var extraction = DynamicSqlScannerV2.Scan(parseResult);
        Assert.Empty(extraction.Findings);

        Assert.Equal(3, extraction.AnalyzableScripts.Count);

        var result = DynamicSqlPipeline.Analyze(extraction.AnalyzableScripts, catalog, lineage);

        Assert.Equal(3, result.Findings.Count);
        Assert.Single(result.Findings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable && f.Reason == "symbolic-value-not-positionable:whole-statement");
        Assert.Equal(2, result.Findings.Count(f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral));

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.ScanForced, typedFinding.Verdict);
        Assert.Equal("Col", typedFinding.Column.ColumnName);
        Assert.True(typedFinding.Column.Indexed);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, result.TypedFindings);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task Analyze_ExecBuiltFromSelectAssignmentSourceColumn_UsedInPredicate_ScanForced_OracleConfirmed()
    {

        var (catalog, lineage) = BuildCatalog();

        var appSql = """
            CREATE PROCEDURE dbo.usp_Scratch AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX)
                SELECT @sql = N'SELECT Col FROM dbo.T WHERE Col = N''' + Col + N''''
                FROM dbo.T
                EXEC(@sql)
            END
            """;
        var parseResult = SqlScriptParser.ParseText("app.sql", appSql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var extraction = DynamicSqlScannerV2.Scan(parseResult, catalog: catalog);
        Assert.Empty(extraction.Findings);
        var script = Assert.Single(extraction.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal("Col", typedFinding.Column.ColumnName);
        Assert.Equal(Verdict.ScanForced, typedFinding.Verdict);
        Assert.Equal(FindingConfidence.Medium, typedFinding.Confidence);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [typedFinding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task Analyze_ExecBuiltFromCursorFetchedNvarcharVariable_UsedInPredicate_ScanForced_OracleConfirmed()
    {

        var (catalog, lineage) = BuildCatalog();

        var appSql = """
            CREATE PROCEDURE dbo.usp_Scratch AS
            BEGIN
                DECLARE @Value NVARCHAR(10) = NULL
                DECLARE @sql NVARCHAR(MAX)
                DECLARE cur CURSOR FOR SELECT SomeCol FROM dbo.SomeOtherTable
                OPEN cur
                FETCH NEXT FROM cur INTO @Value
                WHILE (@@FETCH_STATUS = 0)
                BEGIN
                    SET @sql = N'SELECT Col FROM dbo.T WHERE Col = N''' + @Value + N''''
                    EXEC(@sql)
                    FETCH NEXT FROM cur INTO @Value
                END
                CLOSE cur
                DEALLOCATE cur
            END
            """;
        var parseResult = SqlScriptParser.ParseText("app.sql", appSql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var extraction = DynamicSqlScannerV2.Scan(parseResult, callGraph: new ProcCallGraph([]));
        Assert.Empty(extraction.Findings);
        var script = Assert.Single(extraction.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal("Col", typedFinding.Column.ColumnName);
        Assert.True(typedFinding.Column.Indexed);
        Assert.Equal(Verdict.ScanForced, typedFinding.Verdict);
        Assert.Equal(FindingConfidence.Medium, typedFinding.Confidence);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [typedFinding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public void Analyze_LiteralWithTier1AndExpressionDerivedPredicates_RemapsBothToSourceLine()
    {
        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText(
            "app.sql",
            "EXEC('SELECT Col FROM dbo.T WHERE YEAR(CreatedAt) = 2020');\n" +
            "EXEC('SELECT ColAsInt FROM dbo.vw_T WHERE ColAsInt = 1');");
        Assert.False(parseResult.HasErrors);

        var extraction = DynamicSqlScannerV2.Scan(parseResult);
        Assert.Equal(2, extraction.AnalyzableScripts.Count);

        var result = DynamicSqlPipeline.Analyze(extraction.AnalyzableScripts, catalog, lineage);

        var tier1Finding = Assert.Single(result.Tier1Findings);
        Assert.Equal(SargabilityFindingKind.DateFunctionOnColumn, tier1Finding.Kind);
        Assert.Equal("app.sql", tier1Finding.SourcePath);
        Assert.Equal(1, tier1Finding.Line);
        Assert.NotNull(tier1Finding.DynamicSqlCallSite);

        var expressionFinding = Assert.Single(result.ExpressionDerivedFindings);
        Assert.Equal("app.sql", expressionFinding.SourcePath);
        Assert.Equal(2, expressionFinding.Line);
        Assert.NotNull(expressionFinding.DynamicSqlCallSite);
    }

    [Fact]
    public async Task Analyze_SpExecuteSqlWithDeclaredNvarcharParam_VarcharColumn_ScanForced_OracleConfirmed()
    {

        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText(
            "app.sql",
            "EXEC sp_executesql N'SELECT Col FROM dbo.T WHERE Col = @DisplayName', " +
            "N'@DisplayName nvarchar(40)', @DisplayName = N'x';");
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var script = Assert.Single(DynamicSqlScannerV2.Scan(parseResult).AnalyzableScripts);
        Assert.Contains("@DisplayName", script.ParameterDeclarationText);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.ScanForced, typedFinding.Verdict);
        Assert.Equal("Col", typedFinding.Column.ColumnName);
        Assert.True(typedFinding.Column.Indexed);
        Assert.NotNull(typedFinding.DynamicSqlCallSite);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [typedFinding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public void Analyze_SpExecuteSqlWithoutParamsDeclaration_UndeclaredParameterIsUnknownNotGuessed()
    {

        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText(
            "app.sql", "EXEC sp_executesql N'SELECT Col FROM dbo.T WHERE Col = @DisplayName';");
        Assert.False(parseResult.HasErrors);

        var script = Assert.Single(DynamicSqlScannerV2.Scan(parseResult).AnalyzableScripts);
        Assert.Null(script.ParameterDeclarationText);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.Unknown, typedFinding.Verdict);
    }

    [Fact]
    public void Analyze_SpExecuteSqlWithNonLiteralParamsDeclaration_FallsBackToNoDeclaredTypes()
    {

        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText(
            "app.sql",
            "CREATE PROCEDURE dbo.usp_Test @paramsDecl NVARCHAR(MAX) AS BEGIN " +
            "EXEC sp_executesql N'SELECT Col FROM dbo.T WHERE Col = @DisplayName', @paramsDecl, @DisplayName = N'x'; " +
            "END;");
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var script = Assert.Single(DynamicSqlScannerV2.Scan(parseResult).AnalyzableScripts);
        Assert.Null(script.ParameterDeclarationText);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.Unknown, typedFinding.Verdict);
    }

    [Fact]
    public async Task Analyze_TierCAccumulatedAcrossMultipleSourceLines_RemapsFindingToAssigningLine_OracleConfirmed()
    {

        var (catalog, lineage) = BuildCatalog();

        var appSql =
            "CREATE PROCEDURE dbo.usp_Find\n" +
            "AS\n" +
            "BEGIN\n" +
            "    DECLARE @sql NVARCHAR(MAX) = N'SELECT Col FROM dbo.T ';\n" +
            "    SET @sql = @sql + N'WHERE Col = N''x''';\n" +
            "    EXEC(@sql);\n" +
            "END\n";
        var parseResult = SqlScriptParser.ParseText("app.sql", appSql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var extraction = DynamicSqlScannerV2.Scan(parseResult);
        Assert.Empty(extraction.Findings);
        var script = Assert.Single(extraction.AnalyzableScripts);
        Assert.Equal("SELECT Col FROM dbo.T WHERE Col = N'x'", script.InnerText);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var dynamicFinding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.AnalyzedLiteral, dynamicFinding.Outcome);
        Assert.Equal(6, dynamicFinding.Line);

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.ScanForced, typedFinding.Verdict);
        Assert.Equal("app.sql", typedFinding.SourcePath);
        Assert.Equal(5, typedFinding.Line);
        Assert.NotNull(typedFinding.DynamicSqlCallSite);
        Assert.Equal(6, typedFinding.DynamicSqlCallSite!.Value.Line);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [typedFinding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    private static string WrapInExecLiteral(string sql) => $"EXEC('{sql.Replace("'", "''", StringComparison.Ordinal)}')";

    private static string NestExecChain(string innermostSql, int levels)
    {
        var text = innermostSql;
        for (var i = 0; i < levels; i++)
        {
            text = WrapInExecLiteral(text);
        }

        return text;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Analyze_NestedExecChainWithinDepthLimit_FullyResolvesToScanForced_OracleConfirmed(int levels)
    {

        var (catalog, lineage) = BuildCatalog();

        var innermost = "SELECT Col FROM dbo.T WHERE Col = N'x'";
        var appSql = NestExecChain(innermost, levels) + ";";
        var parseResult = SqlScriptParser.ParseText("app.sql", appSql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var topLevelScript = Assert.Single(DynamicSqlScannerV2.Scan(parseResult).AnalyzableScripts);
        var result = DynamicSqlPipeline.Analyze([topLevelScript], catalog, lineage);

        Assert.Equal(levels, result.Findings.Count(f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral));
        Assert.DoesNotContain(result.Findings, f => f.Outcome != DynamicSqlOutcome.AnalyzedLiteral);
        Assert.All(result.Findings, f => Assert.Equal("app.sql", f.SourcePath));

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.ScanForced, typedFinding.Verdict);
        Assert.Equal("Col", typedFinding.Column.ColumnName);
        Assert.Equal("app.sql", typedFinding.SourcePath);
        Assert.NotNull(typedFinding.DynamicSqlCallSite);
        Assert.Equal("app.sql", typedFinding.DynamicSqlCallSite!.Value.SourcePath);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [typedFinding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public void Analyze_NestedExecChain_RemapsTier1AndExpressionDerivedFindingsToo()
    {

        var (catalog, lineage) = BuildCatalog();

        var innermostTier1 = "SELECT Col FROM dbo.T WHERE YEAR(CreatedAt) = 2020";
        var appSqlTier1 = NestExecChain(innermostTier1, 2) + ";";
        var tier1ParseResult = SqlScriptParser.ParseText("app.sql", appSqlTier1);
        Assert.False(tier1ParseResult.HasErrors);
        var tier1Script = Assert.Single(DynamicSqlScannerV2.Scan(tier1ParseResult).AnalyzableScripts);
        var tier1Result = DynamicSqlPipeline.Analyze([tier1Script], catalog, lineage);

        var tier1Finding = Assert.Single(tier1Result.Tier1Findings);
        Assert.Equal(SargabilityFindingKind.DateFunctionOnColumn, tier1Finding.Kind);
        Assert.Equal("app.sql", tier1Finding.SourcePath);
        Assert.NotNull(tier1Finding.DynamicSqlCallSite);
        Assert.Equal("app.sql", tier1Finding.DynamicSqlCallSite!.Value.SourcePath);

        var innermostExpressionDerived = "SELECT ColAsInt FROM dbo.vw_T WHERE ColAsInt = 1";
        var appSqlExpr = NestExecChain(innermostExpressionDerived, 2) + ";";
        var exprParseResult = SqlScriptParser.ParseText("app.sql", appSqlExpr);
        Assert.False(exprParseResult.HasErrors);
        var exprScript = Assert.Single(DynamicSqlScannerV2.Scan(exprParseResult).AnalyzableScripts);
        var exprResult = DynamicSqlPipeline.Analyze([exprScript], catalog, lineage);

        var expressionFinding = Assert.Single(exprResult.ExpressionDerivedFindings);
        Assert.Equal("app.sql", expressionFinding.SourcePath);
        Assert.NotNull(expressionFinding.DynamicSqlCallSite);
        Assert.Equal("app.sql", expressionFinding.DynamicSqlCallSite!.Value.SourcePath);
    }

    [Fact]
    public void Analyze_NestedExecChainBeyondDepthLimit_ReportsMaxDepthExceededNotSilentlyDropped()
    {

        var (catalog, lineage) = BuildCatalog();

        var innermost = "SELECT Col FROM dbo.T WHERE Col = N'x'";
        var appSql = NestExecChain(innermost, 6) + ";";
        var parseResult = SqlScriptParser.ParseText("app.sql", appSql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var topLevelScript = Assert.Single(DynamicSqlScannerV2.Scan(parseResult).AnalyzableScripts);
        var result = DynamicSqlPipeline.Analyze([topLevelScript], catalog, lineage);

        var depthExceeded = Assert.Single(result.Findings, f => f.Reason == "max-nesting-depth-exceeded");
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, depthExceeded.Outcome);
        Assert.Equal("app.sql", depthExceeded.SourcePath);

        Assert.Equal(5, result.Findings.Count(f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral));
        Assert.Empty(result.TypedFindings);
    }

    [Fact]
    public void Analyze_ProvablyConstantButNotValidTSql_ReportsInnerParseFailed()
    {
        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText("app.sql", "EXEC('THIS IS NOT $$$ valid T-SQL (((');");
        Assert.False(parseResult.HasErrors);

        var extraction = DynamicSqlScannerV2.Scan(parseResult);
        var script = Assert.Single(extraction.AnalyzableScripts);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.InnerParseFailed, finding.Outcome);
        Assert.NotNull(finding.Reason);
        Assert.Empty(result.TypedFindings);
    }

    [Fact]
    public void Analyze_UnsubstitutedTemplatePlaceholderInLiteral_ReportsDistinctReasonNotRawParseError()
    {

        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText("app.sql", "EXEC('UPDATE dbo.Foo SET Col = $Signature$ WHERE Id = 1');");
        Assert.False(parseResult.HasErrors);

        var extraction = DynamicSqlScannerV2.Scan(parseResult);
        var script = Assert.Single(extraction.AnalyzableScripts);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("template-placeholder-not-instantiated", finding.Reason);
        Assert.Empty(result.TypedFindings);
    }

    [Fact]
    public void Analyze_LiteralWithNoPredicates_ProducesAnalyzedFindingAndNoDownstreamFindings()
    {
        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText("app.sql", "EXEC('SELECT 1');");
        var script = Assert.Single(DynamicSqlScannerV2.Scan(parseResult).AnalyzableScripts);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.AnalyzedLiteral, finding.Outcome);
        Assert.Null(finding.Reason);
        Assert.Empty(result.TypedFindings);
        Assert.Empty(result.Tier1Findings);
        Assert.Empty(result.ExpressionDerivedFindings);
    }

    [Fact]
    public async Task Analyze_LiteralWithCte_ResolvesCteColumnThroughToBaseTable_OracleConfirmed()
    {

        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText(
            "app.sql",
            "EXEC('WITH cte AS (SELECT Col FROM dbo.T) SELECT Col FROM cte WHERE Col = N''x''');");
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var script = Assert.Single(DynamicSqlScannerV2.Scan(parseResult).AnalyzableScripts);
        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal("dbo.T", typedFinding.Column.TableQualifiedName);
        Assert.Equal("Col", typedFinding.Column.ColumnName);
        Assert.True(typedFinding.Column.Indexed);
        Assert.Equal(Verdict.ScanForced, typedFinding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [typedFinding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task Analyze_LiteralThroughTwoNestedViewLayers_ResolvesToBaseColumnAtDepthTwo_ScanForced_OracleConfirmed()
    {

        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText(
            "app.sql",
            "EXEC('SELECT Col FROM dbo.vw_T_L2 WHERE Col = N''x''');");
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var script = Assert.Single(DynamicSqlScannerV2.Scan(parseResult).AnalyzableScripts);
        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal("dbo.T", typedFinding.Column.TableQualifiedName);
        Assert.Equal("Col", typedFinding.Column.ColumnName);
        Assert.True(typedFinding.Column.Indexed);
        Assert.Equal(2, typedFinding.Column.Depth);
        Assert.Equal(Verdict.ScanForced, typedFinding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [typedFinding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public void Analyze_LiteralWithCteShadowingRealTable_ResolvesToCteNotTheTable()
    {

        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText(
            "app.sql",
            "EXEC('WITH T AS (SELECT CAST(Col AS INT) AS Col FROM dbo.T) SELECT Col FROM T WHERE Col = 1');");
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var script = Assert.Single(DynamicSqlScannerV2.Scan(parseResult).AnalyzableScripts);
        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        Assert.Empty(result.TypedFindings);
    }

    [Fact]
    public async Task Analyze_ProcParamNoKnownCaller_PlaceholderInsideNvarcharLiteral_ScanForced_OracleConfirmed()
    {

        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText(
            "app.sql",
            "CREATE PROCEDURE dbo.usp_FindByCol @Value NVARCHAR(10) AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT Col FROM dbo.T WHERE Col = N''' + @Value + N''''; EXEC(@sql); END;");
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var script = Assert.Single(DynamicSqlScannerV2.Scan(parseResult, callGraph: new ProcCallGraph([])).AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal("Col", typedFinding.Column.ColumnName);
        Assert.True(typedFinding.Column.Indexed);
        Assert.Equal(Verdict.ScanForced, typedFinding.Verdict);
        Assert.Equal(FindingConfidence.Medium, typedFinding.Confidence);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [typedFinding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task Analyze_ProcParamNoKnownCaller_PlaceholderInsideVarcharLiteral_SeekPreserved_OracleConfirmed()
    {

        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText(
            "app.sql",
            "CREATE PROCEDURE dbo.usp_FindByCol @Value NVARCHAR(10) AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT Col FROM dbo.T WHERE Col = ''' + @Value + N''''; EXEC(@sql); END;");
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var script = Assert.Single(DynamicSqlScannerV2.Scan(parseResult, callGraph: new ProcCallGraph([])).AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal("Col", typedFinding.Column.ColumnName);
        Assert.Equal(Verdict.SeekPreserved, typedFinding.Verdict);
        Assert.Equal(FindingConfidence.Medium, typedFinding.Confidence);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [typedFinding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task Analyze_ProcParamNoKnownCaller_PlaceholderTokenLengthDiffers_SameConfirmedVerdict()
    {

        var (catalog, lineage) = BuildCatalog();

        var padding = new string('\n', 50);
        var parseResult = SqlScriptParser.ParseText(
            "app.sql",
            padding +
            "CREATE PROCEDURE dbo.usp_FindByCol @Value NVARCHAR(10) AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT Col FROM dbo.T WHERE Col = N''' + @Value + N''''; EXEC(@sql); END;");
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var script = Assert.Single(DynamicSqlScannerV2.Scan(parseResult, callGraph: new ProcCallGraph([])).AnalyzableScripts);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.ScanForced, typedFinding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [typedFinding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task Analyze_ProcParamNoKnownCaller_UpperOfPlaceholderInsideVarcharLiteral_SeekPreserved_OracleConfirmed()
    {
        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText(
            "app.sql",
            "CREATE PROCEDURE dbo.usp_FindByCol @Value NVARCHAR(10) AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT Col FROM dbo.T WHERE Col = ''' + UPPER(@Value) + N''''; EXEC(@sql); END;");
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var script = Assert.Single(DynamicSqlScannerV2.Scan(parseResult, callGraph: new ProcCallGraph([])).AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal("Col", typedFinding.Column.ColumnName);
        Assert.Equal(Verdict.SeekPreserved, typedFinding.Verdict);
        Assert.Equal(FindingConfidence.Medium, typedFinding.Confidence);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [typedFinding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task Analyze_ProcParamNoKnownCaller_CastOfPlaceholderInsideNvarcharLiteral_ScanForced_OracleConfirmed()
    {

        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText(
            "app.sql",
            "CREATE PROCEDURE dbo.usp_FindByCol @Value NVARCHAR(10) AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT Col FROM dbo.T WHERE Col = N''' + CAST(@Value AS VARCHAR(10)) + N''''; EXEC(@sql); END;");
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var script = Assert.Single(DynamicSqlScannerV2.Scan(parseResult, callGraph: new ProcCallGraph([])).AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal("Col", typedFinding.Column.ColumnName);
        Assert.Equal(Verdict.ScanForced, typedFinding.Verdict);
        Assert.Equal(FindingConfidence.Medium, typedFinding.Confidence);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [typedFinding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task Analyze_NCharCrLfAndCoalesceSplicedIntoLiteral_ScanForced_OracleConfirmed()
    {

        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText(
            "app.sql",
            "DECLARE @CrLf NVARCHAR(2) = NCHAR(13) + NCHAR(10); " +
            "DECLARE @suffix NVARCHAR(20) = N'x'; " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT Col FROM dbo.T' + @CrLf + N'WHERE Col = N''' + COALESCE(@suffix, N'fallback') + N'''';" +
            "EXEC(@sql);");
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var script = Assert.Single(DynamicSqlScannerV2.Scan(parseResult).AnalyzableScripts);
        Assert.Equal("SELECT Col FROM dbo.T\r\nWHERE Col = N'x'", script.InnerText);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal("Col", typedFinding.Column.ColumnName);
        Assert.Equal(Verdict.ScanForced, typedFinding.Verdict);
        Assert.True(typedFinding.Column.Indexed);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [typedFinding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task Analyze_NewIdCastToStringInPredicate_ScanForced_OracleConfirmed()
    {

        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText(
            "app.sql",
            "CREATE PROCEDURE dbo.usp_FindByNewId AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT Col FROM dbo.T WHERE Col = N''' + CAST(NEWID() AS NVARCHAR(36)) + N''''; EXEC(@sql); END;");
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var script = Assert.Single(DynamicSqlScannerV2.Scan(parseResult).AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal("Col", typedFinding.Column.ColumnName);
        Assert.Equal(Verdict.ScanForced, typedFinding.Verdict);
        Assert.Equal(FindingConfidence.Medium, typedFinding.Confidence);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [typedFinding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task Analyze_GetDateConvertedToStringInPredicate_ScanForced_OracleConfirmed()
    {

        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText(
            "app.sql",
            "CREATE PROCEDURE dbo.usp_FindByGetDate AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT Col FROM dbo.T WHERE Col = N''' + CONVERT(VARCHAR(30), GETDATE()) + N''''; EXEC(@sql); END;");
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var script = Assert.Single(DynamicSqlScannerV2.Scan(parseResult).AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal("Col", typedFinding.Column.ColumnName);
        Assert.Equal(Verdict.ScanForced, typedFinding.Verdict);
        Assert.Equal(FindingConfidence.Medium, typedFinding.Confidence);

        var results2 = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [typedFinding]);
        PipelineOracleVerification.AssertAllConfirmed(results2);
    }

    [Fact]
    public async Task Analyze_CatchBlockReferencesVariableDeclaredOnlyInTry_ScanForced_OracleConfirmed()
    {

        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText(
            "app.sql",
            """
            CREATE PROCEDURE dbo.usp_FindWithRetry AS
            BEGIN
                BEGIN TRY
                    DECLARE @filterValue NVARCHAR(20) = N'x'
                    EXEC('SELECT Col FROM dbo.T WHERE Col = N''' + @filterValue + '''')
                END TRY
                BEGIN CATCH
                    EXEC('SELECT Col FROM dbo.T WHERE Col = N''' + @filterValue + '''')
                END CATCH
            END
            """);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var extraction = DynamicSqlScannerV2.Scan(parseResult);
        Assert.Empty(extraction.Findings);
        Assert.Equal(2, extraction.AnalyzableScripts.Count);

        var result = DynamicSqlPipeline.Analyze(extraction.AnalyzableScripts, catalog, lineage);

        Assert.Equal(2, result.TypedFindings.Count);
        Assert.All(result.TypedFindings, f =>
        {
            Assert.Equal("Col", f.Column.ColumnName);
            Assert.Equal(Verdict.ScanForced, f.Verdict);
        });
        Assert.Contains(result.TypedFindings, f => f.Confidence == FindingConfidence.High);
        Assert.Contains(result.TypedFindings, f => f.Confidence == FindingConfidence.Medium);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, result.TypedFindings);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task Analyze_ProcParamNoKnownCaller_MixedIdentifierAndQuotedPlaceholdersInOneStatement_ScanForced_OracleConfirmed()
    {

        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText(
            "app.sql",
            "CREATE PROCEDURE dbo.usp_JoinAndCheck @LogTableName SYSNAME, @Value NVARCHAR(10) AS " +
            "BEGIN " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT t.Col FROM dbo.T AS t CROSS JOIN ' + QUOTENAME(@LogTableName) + " +
            "N' AS lt WHERE t.Col = N''' + @Value + N''''; " +
            "EXEC(@sql); " +
            "END;");
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var script = Assert.Single(DynamicSqlScannerV2.Scan(parseResult, callGraph: new ProcCallGraph([])).AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal("Col", typedFinding.Column.ColumnName);
        Assert.Equal(Verdict.ScanForced, typedFinding.Verdict);
        Assert.Equal(FindingConfidence.Medium, typedFinding.Confidence);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [typedFinding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task Analyze_SymbolicColumnNameInOrderByPosition_StillFindsUnrelatedLiteralWherePredicate_ScanForced_OracleConfirmed()
    {

        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText(
            "app.sql",
            "CREATE PROCEDURE dbo.usp_Sorted @Name SYSNAME AS " +
            "BEGIN " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT Col FROM dbo.T WHERE Col = N''x'''; " +
            "SET @sql = @sql + N' ORDER BY ' + @Name; " +
            "EXEC(@sql); " +
            "END;");
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var script = Assert.Single(DynamicSqlScannerV2.Scan(parseResult, callGraph: new ProcCallGraph([])).AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var dynamicFinding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.AnalyzedLiteral, dynamicFinding.Outcome);

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal("Col", typedFinding.Column.ColumnName);
        Assert.Equal(Verdict.ScanForced, typedFinding.Verdict);
        Assert.Equal(FindingConfidence.Medium, typedFinding.Confidence);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [typedFinding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }
}
