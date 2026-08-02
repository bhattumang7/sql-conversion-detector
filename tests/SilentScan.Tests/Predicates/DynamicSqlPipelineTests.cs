using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// End-to-end tests for <see cref="DynamicSqlPipeline"/>: real ScriptDOM parses feeding a real
/// <see cref="DynamicSqlScanner"/> extraction into the real catalog/lineage/predicate pipeline,
/// checking that findings inside a folded dynamic SQL string land back on their true source
/// line - including the two cases that break naive index math: a literal spanning multiple
/// source lines, and one containing an escaped quote. Verdict-bearing (ScanForced) findings are
/// additionally confirmed against the real oracle (CLAUDE.md: verify the real thing) - the
/// dynamic-SQL folding/remapping machinery is provenance-only, so the same
/// dbo.T/dbo.vw_T schema deployed below serves every test in this class regardless of how many
/// EXEC/sp_executesql layers the predicate was folded through to reach it.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class DynamicSqlPipelineTests : OracleTestFixture
{
    private const string SchemaSql =
        "CREATE TABLE dbo.T (Col VARCHAR(10) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, CreatedAt DATETIME NOT NULL); " +
        "CREATE INDEX IX_T_Col ON dbo.T(Col); \n" +
        "GO\n" +
        "CREATE VIEW dbo.vw_T AS SELECT CAST(Col AS INT) AS ColAsInt FROM dbo.T;";

    protected override string DatabaseName => nameof(DynamicSqlPipelineTests);

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

        var extraction = DynamicSqlScanner.Scan(parseResult);
        Assert.Empty(extraction.Findings);
        var script = Assert.Single(extraction.AnalyzableScripts);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var dynamicFinding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.AnalyzedLiteral, dynamicFinding.Outcome);
        Assert.Equal(4, dynamicFinding.Line); // the EXEC( call site itself

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.ScanForced, typedFinding.Verdict);
        Assert.Equal("app.sql", typedFinding.SourcePath);
        Assert.Equal(5, typedFinding.Line); // "WHERE Col = ..." is on the second source line
        Assert.NotNull(typedFinding.DynamicSqlCallSite);
        Assert.Equal(4, typedFinding.DynamicSqlCallSite!.Value.Line);

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

        var extraction = DynamicSqlScanner.Scan(parseResult);
        Assert.Equal(2, extraction.AnalyzableScripts.Count);

        var result = DynamicSqlPipeline.Analyze(extraction.AnalyzableScripts, catalog, lineage);

        var tier1Finding = Assert.Single(result.Tier1Findings);
        Assert.Equal(SargabilityFindingKind.FunctionWrappedColumn, tier1Finding.Kind);
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
        // Tier B: the classic ORM-generated shape - sp_executesql's own params declaration
        // string is exact, better type info than most static SQL gets. Col is
        // VARCHAR/SQL_* collation, @DisplayName is declared nvarchar - column-side
        // conversion, so ScanForced, exactly like the same predicate written statically.
        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText(
            "app.sql",
            "EXEC sp_executesql N'SELECT Col FROM dbo.T WHERE Col = @DisplayName', " +
            "N'@DisplayName nvarchar(40)', @DisplayName = N'x';");
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var script = Assert.Single(DynamicSqlScanner.Scan(parseResult).AnalyzableScripts);
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
        // No second argument at all - CLAUDE.md's "never guess": @DisplayName's type is
        // unknowable, so the predicate reports Unknown rather than assuming a match.
        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText(
            "app.sql", "EXEC sp_executesql N'SELECT Col FROM dbo.T WHERE Col = @DisplayName';");
        Assert.False(parseResult.HasErrors);

        var script = Assert.Single(DynamicSqlScanner.Scan(parseResult).AnalyzableScripts);
        Assert.Null(script.ParameterDeclarationText);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.Unknown, typedFinding.Verdict);
    }

    [Fact]
    public void Analyze_SpExecuteSqlWithNonLiteralParamsDeclaration_FallsBackToNoDeclaredTypes()
    {
        // @paramsDecl is a proc PARAMETER, not a local straight-line DECLARE, so Tier C
        // correctly can't fold it either - this is the genuine "we can't know the declared
        // types" case, distinct from a foldable local variable.
        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText(
            "app.sql",
            "CREATE PROCEDURE dbo.usp_Test @paramsDecl NVARCHAR(MAX) AS BEGIN " +
            "EXEC sp_executesql N'SELECT Col FROM dbo.T WHERE Col = @DisplayName', @paramsDecl, @DisplayName = N'x'; " +
            "END;");
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var script = Assert.Single(DynamicSqlScanner.Scan(parseResult).AnalyzableScripts);
        Assert.Null(script.ParameterDeclarationText);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.Unknown, typedFinding.Verdict);
    }

    [Fact]
    public async Task Analyze_TierCAccumulatedAcrossMultipleSourceLines_RemapsFindingToAssigningLine_OracleConfirmed()
    {
        // Tier C's folded text is stitched from segments scattered across several DECLARE/SET
        // statements at different source lines - the finding it produces must land on the
        // specific line that contributed the offending text, not the EXEC call site.
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

        var extraction = DynamicSqlScanner.Scan(parseResult);
        Assert.Empty(extraction.Findings);
        var script = Assert.Single(extraction.AnalyzableScripts);
        Assert.Equal("SELECT Col FROM dbo.T WHERE Col = N'x'", script.InnerText);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var dynamicFinding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.AnalyzedLiteral, dynamicFinding.Outcome);
        Assert.Equal(6, dynamicFinding.Line); // the EXEC( call site

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.ScanForced, typedFinding.Verdict);
        Assert.Equal("app.sql", typedFinding.SourcePath);
        Assert.Equal(5, typedFinding.Line); // the SET statement that contributed "WHERE Col = ..."
        Assert.NotNull(typedFinding.DynamicSqlCallSite);
        Assert.Equal(6, typedFinding.DynamicSqlCallSite!.Value.Line);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [typedFinding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    /// <summary>Wraps <paramref name="sql"/> as the literal argument of one more level of EXEC('...'), escaping embedded quotes - builds an N-level-nested dynamic SQL chain without hand-deriving the quote-doubling at each level.</summary>
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
        // Answers "how about 2/3/4/5 levels of recursion": each level is EXEC('...') wrapping
        // the next, with the real implicit-conversion predicate at the innermost level. All
        // of these are within MaxNestingDepth (5) and must fully resolve - not just detect
        // that dynamic SQL exists N levels down, but actually reparse and analyze it.
        var (catalog, lineage) = BuildCatalog();

        var innermost = "SELECT Col FROM dbo.T WHERE Col = N'x'";
        var appSql = NestExecChain(innermost, levels) + ";";
        var parseResult = SqlScriptParser.ParseText("app.sql", appSql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var topLevelScript = Assert.Single(DynamicSqlScanner.Scan(parseResult).AnalyzableScripts);
        var result = DynamicSqlPipeline.Analyze([topLevelScript], catalog, lineage);

        // One AnalyzedLiteral DynamicSqlFinding per EXEC level in the chain.
        Assert.Equal(levels, result.Findings.Count(f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral));
        Assert.DoesNotContain(result.Findings, f => f.Outcome != DynamicSqlOutcome.AnalyzedLiteral);
        Assert.All(result.Findings, f => Assert.Equal("app.sql", f.SourcePath));

        var typedFinding = Assert.Single(result.TypedFindings);
        Assert.Equal(Verdict.ScanForced, typedFinding.Verdict);
        Assert.Equal("Col", typedFinding.Column.ColumnName);
        Assert.Equal("app.sql", typedFinding.SourcePath);
        Assert.NotNull(typedFinding.DynamicSqlCallSite);
        Assert.Equal("app.sql", typedFinding.DynamicSqlCallSite!.Value.SourcePath);

        // The nesting depth is purely a provenance/remapping concern - the underlying comparison
        // the oracle probes is the same "Col = N'x'" against dbo.T at every depth in this theory.
        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [typedFinding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public void Analyze_NestedExecChain_RemapsTier1AndExpressionDerivedFindingsToo()
    {
        // The remap path for Tier1/expression-derived findings produced at a nested level is
        // separate code from the typed-predicate path exercised above - cover it directly
        // with a 2-level chain whose innermost query hits both.
        var (catalog, lineage) = BuildCatalog();

        var innermostTier1 = "SELECT Col FROM dbo.T WHERE YEAR(CreatedAt) = 2020";
        var appSqlTier1 = NestExecChain(innermostTier1, 2) + ";";
        var tier1ParseResult = SqlScriptParser.ParseText("app.sql", appSqlTier1);
        Assert.False(tier1ParseResult.HasErrors);
        var tier1Script = Assert.Single(DynamicSqlScanner.Scan(tier1ParseResult).AnalyzableScripts);
        var tier1Result = DynamicSqlPipeline.Analyze([tier1Script], catalog, lineage);

        var tier1Finding = Assert.Single(tier1Result.Tier1Findings);
        Assert.Equal(SargabilityFindingKind.FunctionWrappedColumn, tier1Finding.Kind);
        Assert.Equal("app.sql", tier1Finding.SourcePath);
        Assert.NotNull(tier1Finding.DynamicSqlCallSite);
        Assert.Equal("app.sql", tier1Finding.DynamicSqlCallSite!.Value.SourcePath);

        var innermostExpressionDerived = "SELECT ColAsInt FROM dbo.vw_T WHERE ColAsInt = 1";
        var appSqlExpr = NestExecChain(innermostExpressionDerived, 2) + ";";
        var exprParseResult = SqlScriptParser.ParseText("app.sql", appSqlExpr);
        Assert.False(exprParseResult.HasErrors);
        var exprScript = Assert.Single(DynamicSqlScanner.Scan(exprParseResult).AnalyzableScripts);
        var exprResult = DynamicSqlPipeline.Analyze([exprScript], catalog, lineage);

        var expressionFinding = Assert.Single(exprResult.ExpressionDerivedFindings);
        Assert.Equal("app.sql", expressionFinding.SourcePath);
        Assert.NotNull(expressionFinding.DynamicSqlCallSite);
        Assert.Equal("app.sql", expressionFinding.DynamicSqlCallSite!.Value.SourcePath);
    }

    [Fact]
    public void Analyze_NestedExecChainBeyondDepthLimit_ReportsMaxDepthExceededNotSilentlyDropped()
    {
        // One level past MaxNestingDepth (5) - CLAUDE.md's "no silent truncation": the
        // analysis must stop, but it must say so, with a real remapped source location.
        var (catalog, lineage) = BuildCatalog();

        var innermost = "SELECT Col FROM dbo.T WHERE Col = N'x'";
        var appSql = NestExecChain(innermost, 6) + ";";
        var parseResult = SqlScriptParser.ParseText("app.sql", appSql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var topLevelScript = Assert.Single(DynamicSqlScanner.Scan(parseResult).AnalyzableScripts);
        var result = DynamicSqlPipeline.Analyze([topLevelScript], catalog, lineage);

        var depthExceeded = Assert.Single(result.Findings, f => f.Reason == "max-nesting-depth-exceeded");
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, depthExceeded.Outcome);
        Assert.Equal("app.sql", depthExceeded.SourcePath);

        // The 5 levels that WERE within the limit still resolved normally; only the 6th was
        // declined - the whole chain isn't thrown away just because it goes one level too deep.
        Assert.Equal(5, result.Findings.Count(f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral));
        Assert.Empty(result.TypedFindings);
    }

    [Fact]
    public void Analyze_ProvablyConstantButNotValidTSql_ReportsInnerParseFailed()
    {
        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText("app.sql", "EXEC('THIS IS NOT $$$ valid T-SQL (((');");
        Assert.False(parseResult.HasErrors);

        var extraction = DynamicSqlScanner.Scan(parseResult);
        var script = Assert.Single(extraction.AnalyzableScripts);

        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.InnerParseFailed, finding.Outcome);
        Assert.NotNull(finding.Reason);
        Assert.Empty(result.TypedFindings);
    }

    [Fact]
    public void Analyze_LiteralWithNoPredicates_ProducesAnalyzedFindingAndNoDownstreamFindings()
    {
        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText("app.sql", "EXEC('SELECT 1');");
        var script = Assert.Single(DynamicSqlScanner.Scan(parseResult).AnalyzableScripts);

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
        // A reparsed dynamic-SQL fragment goes through the identical TypedPredicateExtractor
        // visitor and CteResolver static SQL uses (docs/coverage-remediation-plan.md Phase
        // 4.3) - there is no separate CTE-handling code path for dynamic SQL, so this was true
        // by construction before this test existed. It just wasn't checked.
        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText(
            "app.sql",
            "EXEC('WITH cte AS (SELECT Col FROM dbo.T) SELECT Col FROM cte WHERE Col = N''x''');");
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var script = Assert.Single(DynamicSqlScanner.Scan(parseResult).AnalyzableScripts);
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
    public void Analyze_LiteralWithCteShadowingRealTable_ResolvesToCteNotTheTable()
    {
        // CTE names shadow catalog objects within their statement's scope (audit-remediation-
        // plan.md Phase 2.4) - proving that shadowing rule also holds inside dynamic SQL, not
        // just static SQL.
        var (catalog, lineage) = BuildCatalog();

        var parseResult = SqlScriptParser.ParseText(
            "app.sql",
            "EXEC('WITH T AS (SELECT CAST(Col AS INT) AS Col FROM dbo.T) SELECT Col FROM T WHERE Col = 1');");
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var script = Assert.Single(DynamicSqlScanner.Scan(parseResult).AnalyzableScripts);
        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        // The CTE's Col is an expression (CAST), not the base dbo.T.Col - so it must not resolve
        // to a BaseColumn typed finding at all; it can only ever surface (if anywhere) as an
        // expression-derived finding, never as a direct column-side verdict against dbo.T.
        Assert.Empty(result.TypedFindings);
    }
}
