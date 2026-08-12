using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Proves the live dynamic-SQL engine survives all the way from a real, hand-written .sql file on
/// disk (tests/SilentScan.Tests/fixtures/dynamic/) through the SAME catalog/lineage/scanner/
/// pipeline wiring production uses, to a real oracle-relevant Verdict - not just that the scanner
/// alone produces an analyzable script (DynamicSqlScannerTests) or that a hand-built inline SQL
/// string reaches a finding (DynamicSqlPipelineTests). Nothing else in the suite currently reads a
/// dynamic-SQL fixture FILE and pushes it through the full pipeline this way. Deliberately
/// Docker-free: DisplayName's VARCHAR/SQL_* vs NVARCHAR column-side-conversion shape is already
/// oracle-confirmed for the identical predicate pattern in DynamicSqlPipelineTests, so these tests
/// assert the resulting Verdict directly rather than re-paying the Docker cost for the same fact.
/// </summary>
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
        // Tier B: sp_executesql's own @params declaration gives exact parameter typing
        // (@Name NVARCHAR(50)) against DisplayName's VARCHAR(50)/SQL_Latin1_General_CP1_CI_AS
        // column - column-side conversion per T-SQL precedence.
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
        // @Name's own DECLARE (NVARCHAR(50)) survives an unmodeled OUTPUT-parameter write as a
        // typed hole spliced into the surrounding known text, not a blanket taint of the whole
        // EXEC argument - the predicate is still real and still column-side-converting.
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
        // The outer EXEC('...') folds to text that itself contains a further EXEC('...') -
        // DynamicSqlPipeline.AnalyzeNested must reparse and recurse into that second layer to
        // reach the real predicate, exactly like the two/three/four/five-level nesting theory in
        // DynamicSqlPipelineTests, here driven from a fixture file instead of an inline builder.
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
        // The THEN branch calls REVERSE (deliberately not folded), but the ELSE branch's own
        // complete, known predicate must still be recovered as a GuardedAlternative rather than
        // discarded just because its sibling branch didn't fold.
        var finding = RunFixtureToSingleTypedFinding("if_else_guarded_alternative_recovery.sql");

        Assert.Equal("DisplayName", finding.Column.ColumnName);
        Assert.Equal("dbo.Customers", finding.Column.TableQualifiedName);
        Assert.True(finding.Column.Indexed);
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }
}
