using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

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

    protected override string Ddl => NvarcharReturningSql;

    [Fact]
    public async Task VarcharColumnAgainstNvarcharReturningUdf_ClassifiesScanForced_OracleConfirmed()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(NvarcharReturningSql, "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"), f => f.Column.ColumnName == "Code");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public void UnqualifiedFunctionCall_ResolvesUnderDefaultDboSchema()
    {

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

        var finding = Assert.Single(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"), f => f.Column.ColumnName == "Code");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
    }

    [Fact]
    public async Task SameFamilyAndCollationAgainstUdf_ClassifiesSeekPreserved()
    {

        var report = await EngineAuthoritativeScan.ScanAsync("""
            CREATE TABLE dbo.Accounts (Code varchar(50) NOT NULL, INDEX IX_Code (Code));
            GO
            CREATE FUNCTION dbo.fn_DefaultVarcharCode() RETURNS varchar(50) AS BEGIN RETURN 'X' END;
            GO
            CREATE PROCEDURE dbo.usp_FindAccount AS
                SELECT Code FROM dbo.Accounts WHERE Code = dbo.fn_DefaultVarcharCode();
            """, "SQL_Latin1_General_CP1_CI_AS");

        Assert.Empty(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"));
        Assert.Equal(1, report.TypedPredicateSummary.SeekPreservedCount);
    }

    [Fact]
    public async Task UnregisteredFunction_StillResolvesUnknown()
    {

        var report = await EngineAuthoritativeScan.ScanAsync(MissingFunctionSql, "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"), f => f.Column.ColumnName == "Code");
        Assert.Equal(Verdict.Unknown, finding.Verdict);
    }
}
