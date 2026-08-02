using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Regression coverage for the scalar UDF return-type gap (formerly pinned in
/// KnownGapCharacterizationTests.ScalarUdfReturnType_IsNotRegistered_ComparisonFallsToUnknown,
/// and called the highest-value single gap by the construct coverage audit): a predicate
/// comparing a column against a scalar user-defined function call must type the function side
/// from its own RETURNS clause, not fall to Unknown for lack of any type at all. Runs through
/// <see cref="ScanReportBuilder"/>, the same entry point production uses.
/// </summary>
public sealed class ScalarUdfPipelineTests
{
    [Fact]
    public void VarcharColumnAgainstNvarcharReturningUdf_ClassifiesScanForced()
    {
        var parseResult = SqlScriptParser.ParseText("scalar_udf.sql", """
            CREATE TABLE dbo.Accounts (Code varchar(50) NOT NULL, INDEX IX_Code (Code));
            GO
            CREATE FUNCTION dbo.fn_DefaultCode() RETURNS nvarchar(50) AS BEGIN RETURN N'X' END;
            GO
            CREATE PROCEDURE dbo.usp_FindAccount AS
                SELECT Code FROM dbo.Accounts WHERE Code = dbo.fn_DefaultCode();
            """);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Code");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
    }

    [Fact]
    public void UnqualifiedFunctionCall_ResolvesUnderDefaultDboSchema()
    {
        // fn_DefaultCode() with no schema qualifier still resolves against dbo.fn_DefaultCode,
        // matching CatalogBuilder's own default-schema convention for unqualified references.
        var parseResult = SqlScriptParser.ParseText("scalar_udf_unqualified.sql", """
            CREATE TABLE dbo.Accounts (Code varchar(50) NOT NULL, INDEX IX_Code (Code));
            GO
            CREATE FUNCTION dbo.fn_DefaultCode() RETURNS nvarchar(50) AS BEGIN RETURN N'X' END;
            GO
            CREATE PROCEDURE dbo.usp_FindAccount AS
                SELECT Code FROM dbo.Accounts WHERE Code = fn_DefaultCode();
            """);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Code");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void SameFamilyAndCollationAgainstUdf_ClassifiesSeekPreserved()
    {
        // A UDF whose RETURNS type matches the column's own family/collation must not be
        // flagged - proves the registry types the operand rather than always forcing a
        // mismatch verdict once a UDF is involved.
        var parseResult = SqlScriptParser.ParseText("scalar_udf_clean.sql", """
            CREATE TABLE dbo.Accounts (Code varchar(50) NOT NULL, INDEX IX_Code (Code));
            GO
            CREATE FUNCTION dbo.fn_DefaultVarcharCode() RETURNS varchar(50) AS BEGIN RETURN 'X' END;
            GO
            CREATE PROCEDURE dbo.usp_FindAccount AS
                SELECT Code FROM dbo.Accounts WHERE Code = dbo.fn_DefaultVarcharCode();
            """);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");

        Assert.Empty(report.TypedFindings);
        Assert.Equal(1, report.TypedPredicateSummary.SeekPreservedCount);
    }

    [Fact]
    public void UnregisteredFunction_StillResolvesUnknown()
    {
        // A function name that was never declared as CREATE FUNCTION anywhere in the scan
        // (typo, or genuinely external) must still resolve Unknown, not silently match some
        // other function's registry entry.
        var parseResult = SqlScriptParser.ParseText("scalar_udf_missing.sql", """
            CREATE TABLE dbo.Accounts (Code varchar(50) NOT NULL, INDEX IX_Code (Code));
            GO
            SELECT Code FROM dbo.Accounts WHERE Code = dbo.fn_NeverDeclared();
            """);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Code");
        Assert.Equal(Verdict.Unknown, finding.Verdict);
    }
}
