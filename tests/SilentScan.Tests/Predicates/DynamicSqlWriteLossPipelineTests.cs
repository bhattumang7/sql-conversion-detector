using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Predicates;

public sealed class DynamicSqlWriteLossPipelineTests
{
    private static DynamicSqlPipelineResult AnalyzeExecOfConstantSql(string innerSql)
    {
        var appSql = $"EXEC('{innerSql.Replace("'", "''", StringComparison.Ordinal)}');";
        var parseResult = SqlScriptParser.ParseText("app.sql", appSql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var script = Assert.Single(DynamicSqlScannerV2.Scan(parseResult).AnalyzableScripts);
        return DynamicSqlPipeline.Analyze([script], catalog, lineage);
    }

    [Fact]
    public void Analyze_ExecOfConstantSqlWithLossyCompoundAssignment_FlagsWriteLoss()
    {
        var result = AnalyzeExecOfConstantSql("DECLARE @v DECIMAL(10,2) = 0; SET @v += 123.456;");

        var dynamicFinding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.AnalyzedLiteral, dynamicFinding.Outcome);

        var finding = Assert.Single(result.WriteLossFindings);
        Assert.Equal(WriteLossKind.NumericScaleNarrowing, finding.Kind);
        Assert.Null(finding.TableQualifiedName);
        Assert.Equal("@v", finding.ColumnName);
    }

    [Fact]
    public void Analyze_ExecOfConstantSqlWithCompoundAssignmentWithinScale_NoWriteLossFinding()
    {
        var result = AnalyzeExecOfConstantSql("DECLARE @v DECIMAL(10,2) = 0; SET @v += 1;");

        Assert.Empty(result.WriteLossFindings);
    }

    [Fact]
    public void Analyze_ExecOfConstantSqlWithStringCompoundAssignmentOverflowingAlone_FlagsWriteLoss()
    {
        var result = AnalyzeExecOfConstantSql("DECLARE @s VARCHAR(5) = 'ab'; SET @s += 'cdefgh';");

        var finding = Assert.Single(result.WriteLossFindings);
        Assert.Equal(WriteLossKind.LengthTruncation, finding.Kind);
        Assert.Null(finding.TableQualifiedName);
        Assert.Equal("@s", finding.ColumnName);
    }

    [Fact]
    public void Analyze_ExecOfConstantSqlWithStringCompoundAssignmentAppendedPartAloneFits_NoWriteLossFinding()
    {
        var result = AnalyzeExecOfConstantSql("DECLARE @s VARCHAR(10) = 'ab'; SET @s += 'cd';");

        Assert.Empty(result.WriteLossFindings);
    }

    [Fact]
    public void Analyze_ExecOfVariableAssignedConstantSqlWithCompoundAssignment_FlagsWriteLoss()
    {
        const string appSql = """
            DECLARE @var1 VARCHAR(MAX);
            SET @var1 = 'DECLARE @s VARCHAR(5) = ''ab''; SET @s += ''cdefgh''; select @s';
            EXEC(@var1);
            """;

        var parseResult = SqlScriptParser.ParseText("app.sql", appSql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var script = Assert.Single(DynamicSqlScannerV2.Scan(parseResult).AnalyzableScripts);
        var result = DynamicSqlPipeline.Analyze([script], catalog, lineage);

        var dynamicFinding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.AnalyzedLiteral, dynamicFinding.Outcome);

        var finding = Assert.Single(result.WriteLossFindings);
        Assert.Equal(WriteLossKind.LengthTruncation, finding.Kind);
        Assert.Null(finding.TableQualifiedName);
        Assert.Equal("@s", finding.ColumnName);
    }

    [Fact]
    public void Analyze_BareExecOfVariableAssignedConstantSql_NotRecognizedAsDynamicSqlCallSite()
    {
        const string appSql = """
            DECLARE @var1 VARCHAR(MAX);
            SET @var1 = 'DECLARE @s VARCHAR(5) = ''ab''; SET @s += ''cdefgh''; select @s';
            EXEC @var1;
            """;

        var parseResult = SqlScriptParser.ParseText("app.sql", appSql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var extraction = DynamicSqlScannerV2.Scan(parseResult);

        Assert.Empty(extraction.AnalyzableScripts);
        Assert.Empty(extraction.Findings);
    }
}
