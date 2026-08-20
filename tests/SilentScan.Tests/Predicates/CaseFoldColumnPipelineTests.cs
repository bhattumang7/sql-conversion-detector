using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 1 "Type-aware upgrade of the sargability stream" #4 -
/// UPPER/LOWER on a column. Oracle-corrected relative to the checklist's original framing: SQL
/// Server does NOT special-case away the wrap for a case-insensitive collation - an indexed
/// UPPER(Code)/LOWER(Code) predicate produces an Index Scan under EITHER collation family, so
/// this stream is syntactic-with-index-weighting (never suppressed by collation), and only the
/// finding's own remediation message changes. Confirms that claim directly against the real
/// oracle, plus the generalized computed-column precision guard
/// (<see cref="ComputedColumnMatcher"/>).
/// </summary>
[Trait("Category", "Oracle")]
public sealed class CaseFoldColumnPipelineTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(CaseFoldColumnPipelineTests);

    private const string CiSql = """
        CREATE TABLE dbo.CiUsers (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, INDEX IX_CiUsers_Code (Code));
        """;

    private const string CsSql = """
        CREATE TABLE dbo.CsUsers (Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CS_AS NOT NULL, INDEX IX_CsUsers_Code (Code));
        """;

    private const string ComputedColumnSql = """
        CREATE TABLE dbo.GuardUsers (
            Code VARCHAR(20) NOT NULL,
            CodeUpper AS UPPER(Code));
        GO
        CREATE INDEX IX_GuardUsers_CodeUpper ON dbo.GuardUsers(CodeUpper);
        GO

        SELECT 1 FROM dbo.GuardUsers WHERE UPPER(Code) = 'ACTIVE';
        """;

    protected override string Ddl => CiSql + "\nGO\n" + CsSql + "\nGO\n" + ComputedColumnSql;

    [Theory]
    [InlineData("CiUsers")]
    [InlineData("CsUsers")]
    public async Task IndexedColumn_UpperWrap_ForcesScanUnderEitherCollation_OracleConfirmed(string table)
    {
        var probe = $"SELECT 1 FROM dbo.{table} WHERE UPPER(Code) = 'ACTIVE';";

        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);

        Assert.Contains("PhysicalOp=\"Index Scan\"", planXml);
    }

    [Fact]
    public void CaseInsensitiveColumn_RemediationSaysWrapIsRedundant()
    {
        var parseResult = SqlScriptParser.ParseText("ci.sql", CiSql + "\nSELECT 1 FROM dbo.CiUsers WHERE UPPER(Code) = 'ACTIVE';");
        Assert.False(parseResult.HasErrors);
        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var findings = NonSargablePredicateScanner.Scan(parseResult, catalog, lineage);

        var finding = Assert.Single(findings, f => f.TableQualifiedName == "dbo.CiUsers");
        Assert.Contains("case-insensitive", finding.Detail);
        Assert.Contains("zero result-set risk", finding.Detail);
    }

    [Fact]
    public void CaseSensitiveColumn_RemediationSaysWrapIsLoadBearing()
    {
        var parseResult = SqlScriptParser.ParseText("cs.sql", CsSql + "\nSELECT 1 FROM dbo.CsUsers WHERE UPPER(Code) = 'ACTIVE';");
        Assert.False(parseResult.HasErrors);
        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var findings = NonSargablePredicateScanner.Scan(parseResult, catalog, lineage);

        var finding = Assert.Single(findings, f => f.TableQualifiedName == "dbo.CsUsers");
        Assert.Contains("case-sensitive", finding.Detail);
        Assert.Contains("load-bearing", finding.Detail);
    }

    [Fact]
    public void MatchingIndexedComputedColumn_SuppressesTheFinding_FileModeCatalog()
    {
        var parseResult = SqlScriptParser.ParseText("guard.sql", ComputedColumnSql);
        Assert.False(parseResult.HasErrors);
        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);

        var findings = NonSargablePredicateScanner.Scan(parseResult, catalog, lineage);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task MatchingIndexedComputedColumn_OracleConfirmsIndexSeek()
    {
        const string probe = "SELECT 1 FROM dbo.GuardUsers WHERE UPPER(Code) = 'ACTIVE';";

        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);

        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
    }
}
