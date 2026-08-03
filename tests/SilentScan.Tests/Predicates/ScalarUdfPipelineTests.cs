using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Regression coverage for the scalar UDF return-type gap (formerly pinned in
/// KnownGapCharacterizationTests.ScalarUdfReturnType_IsNotRegistered_ComparisonFallsToUnknown,
/// and called the highest-value single gap by the construct coverage audit): a predicate
/// comparing a column against a scalar user-defined function call must type the function side
/// from its own RETURNS clause, not fall to Unknown for lack of any type at all. Runs through
/// <see cref="ScanReportBuilder"/>, the same entry point production uses, and the verdict-
/// bearing cases are confirmed against the real oracle (CLAUDE.md: verify the real thing).
/// </summary>
[Trait("Category", "Oracle")]
public sealed class ScalarUdfPipelineTests : OracleTestFixture
{
    private const string NvarcharReturningSql = """
        CREATE TABLE dbo.Accounts (Code varchar(50) NOT NULL, INDEX IX_Code (Code));
        GO
        CREATE FUNCTION dbo.fn_DefaultCode() RETURNS nvarchar(50) AS BEGIN RETURN N'X' END;
        GO
        CREATE PROCEDURE dbo.usp_FindAccount AS
            SELECT Code FROM dbo.Accounts WHERE Code = dbo.fn_DefaultCode();
        """;

    private const string MissingFunctionSql = """
        CREATE TABLE dbo.AccountsMissing (Code varchar(50) NOT NULL, INDEX IX_Code (Code));
        GO
        SELECT Code FROM dbo.AccountsMissing WHERE Code = dbo.fn_NeverDeclared();
        """;

    protected override string DatabaseNameSeed => nameof(ScalarUdfPipelineTests);

    // fn_NeverDeclared() is never CREATEd - dbo.fn_NeverDeclared must NOT be deployed here,
    // since the whole point of UnregisteredFunction_StillResolvesUnknown is that the function
    // genuinely does not exist anywhere in the scanned project.
    protected override string Ddl => NvarcharReturningSql;

    [Fact]
    public async Task VarcharColumnAgainstNvarcharReturningUdf_ClassifiesScanForced_OracleConfirmed()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(NvarcharReturningSql, "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Code");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public void UnqualifiedFunctionCall_ResolvesUnderDefaultDboSchema()
    {
        // fn_DefaultCode() with no schema qualifier still resolves against dbo.fn_DefaultCode,
        // matching CatalogBuilder's own default-schema convention for unqualified references.
        // Parsed-only, via CatalogBuilder directly, not EngineAuthoritativeScan: calling a
        // user scalar function with no schema qualifier is itself rejected by a real SQL Server
        // ("'fn_DefaultCode' is not a recognized function name", verified directly - unlike a
        // built-in function, a user-defined one is never resolved unqualified) - this is
        // legitimate, real-world corpus text (a schema-unqualified UDF call a developer wrote
        // assuming the default schema, common enough that this pass has its own resolution rule
        // for it) that this pass must still type correctly from parsed text, independent of
        // whether that exact call syntax could ever actually execute.
        var sql = """
            CREATE TABLE dbo.Accounts (Code varchar(50) NOT NULL, INDEX IX_Code (Code));
            GO
            CREATE FUNCTION dbo.fn_DefaultCode() RETURNS nvarchar(50) AS BEGIN RETURN N'X' END;
            GO
            CREATE PROCEDURE dbo.usp_FindAccount AS
                SELECT Code FROM dbo.Accounts WHERE Code = fn_DefaultCode();
            """;
        var parseResult = SqlScriptParser.ParseText("udf.sql", sql);
        Assert.Empty(parseResult.Errors);
        var catalog = CatalogBuilder.Build([parseResult], "SQL_Latin1_General_CP1_CI_AS");
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], catalog);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Code");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
    }

    [Fact]
    public async Task SameFamilyAndCollationAgainstUdf_ClassifiesSeekPreserved()
    {
        // A UDF whose RETURNS type matches the column's own family/collation must not be
        // flagged - proves the registry types the operand rather than always forcing a
        // mismatch verdict once a UDF is involved. SeekPreserved makes no CONVERT_IMPLICIT
        // claim, so there's nothing for the plan-XML oracle to confirm here.
        var report = await EngineAuthoritativeScan.ScanAsync("""
            CREATE TABLE dbo.Accounts (Code varchar(50) NOT NULL, INDEX IX_Code (Code));
            GO
            CREATE FUNCTION dbo.fn_DefaultVarcharCode() RETURNS varchar(50) AS BEGIN RETURN 'X' END;
            GO
            CREATE PROCEDURE dbo.usp_FindAccount AS
                SELECT Code FROM dbo.Accounts WHERE Code = dbo.fn_DefaultVarcharCode();
            """, "SQL_Latin1_General_CP1_CI_AS");

        Assert.Empty(report.TypedFindings);
        Assert.Equal(1, report.TypedPredicateSummary.SeekPreservedCount);
    }

    [Fact]
    public async Task UnregisteredFunction_StillResolvesUnknown()
    {
        // A function name that was never declared as CREATE FUNCTION anywhere in the scan
        // (typo, or genuinely external) must still resolve Unknown, not silently match some
        // other function's registry entry. Unknown is a claim about our own uncertainty, not
        // a claim the engine can confirm or deny, so no oracle round-trip here.
        var report = await EngineAuthoritativeScan.ScanAsync(MissingFunctionSql, "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Code");
        Assert.Equal(Verdict.Unknown, finding.Verdict);
    }
}
