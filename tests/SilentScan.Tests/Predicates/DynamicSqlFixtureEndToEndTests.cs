using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Predicates;

public sealed class DynamicSqlFixtureEndToEndTests
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "dynamic");

    private static TypedPredicateFinding RunFixtureToSingleTypedFinding(string fixtureFileName)
    {
        var path = Path.Combine(FixtureDir, fixtureFileName);
        var sql = File.ReadAllText(path);

        var parseResult = SqlScriptParser.ParseText(fixtureFileName, sql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var extraction = DynamicSqlScannerV2.Scan(parseResult, callGraph: new ProcCallGraph([]));
        Assert.Empty(extraction.Findings);
        Assert.NotEmpty(extraction.AnalyzableScripts);

        var pipeline = DynamicSqlPipeline.Analyze(extraction.AnalyzableScripts, catalog, lineage);

        return Assert.Single(pipeline.TypedFindings);
    }

    [Fact]
    public void SpExecuteSqlWithParamTypes_ResolvesDisplayNameColumnSideConversion_ScanForced()
    {

        var finding = RunFixtureToSingleTypedFinding("sp_executesql_with_param_types.sql");

        Assert.Equal("DisplayName", finding.Column.ColumnName);
        Assert.Equal("dbo.Customers", finding.Column.TableQualifiedName);
        Assert.True(finding.Column.Indexed);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.NotNull(finding.DynamicSqlCallSite);
    }

    [Fact]
    public void ExecStringConcatWithHavocBranch_TypedHoleFromUnmodeledWrite_ScanForced()
    {

        var finding = RunFixtureToSingleTypedFinding("exec_string_concat_with_havoc_branch.sql");

        Assert.Equal("DisplayName", finding.Column.ColumnName);
        Assert.Equal("dbo.Customers", finding.Column.TableQualifiedName);
        Assert.True(finding.Column.Indexed);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public void NestedDynamicSqlTwoLevels_RecursesIntoInnerExecScope_ScanForced()
    {

        var finding = RunFixtureToSingleTypedFinding("nested_dynamic_sql_two_levels.sql");

        Assert.Equal("DisplayName", finding.Column.ColumnName);
        Assert.Equal("dbo.Customers", finding.Column.TableQualifiedName);
        Assert.True(finding.Column.Indexed);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.NotNull(finding.DynamicSqlCallSite);
    }

    [Fact]
    public void IfElseGuardedAlternativeRecovery_RecoversKnownSiblingBranch_ScanForced()
    {

        var finding = RunFixtureToSingleTypedFinding("if_else_guarded_alternative_recovery.sql");

        Assert.Equal("DisplayName", finding.Column.ColumnName);
        Assert.Equal("dbo.Customers", finding.Column.TableQualifiedName);
        Assert.True(finding.Column.Indexed);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }
}
