using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class OperatorAndOperandShapeOracleTests : OracleTestFixture
{
    private const string Ddl_ = """
        CREATE TABLE dbo.VarCharWin (Code VARCHAR(20) COLLATE Latin1_General_CI_AS NOT NULL, OtherCode NVARCHAR(20) COLLATE Latin1_General_CI_AS NOT NULL, INDEX IX_Code (Code));
        GO
        CREATE TABLE dbo.NVarCharWin (Code NVARCHAR(20) COLLATE Latin1_General_CI_AS NOT NULL);
        """;

    protected override string DatabaseNameSeed => nameof(OperatorAndOperandShapeOracleTests);

    protected override string Ddl => Ddl_;

    private static async Task<ScanReport> Scan(string sql)
    {
        var report = await EngineAuthoritativeScan.ScanAsync(Ddl_ + "\nGO\n" + sql, "Latin1_General_CI_AS");
        foreach (var file in report.ParseHealth.Files)
        {
            Assert.Empty(file.Errors);
        }

        return report;
    }

    [Fact]
    public async Task VarCharColumnLikeNVarcharVariable_WindowsCollation_IsScanForced_OracleConfirmed()
    {
        var report = await Scan("""
            CREATE PROCEDURE dbo.usp_FindByPrefix @Prefix NVARCHAR(20)
            AS
            BEGIN
                SELECT Code FROM dbo.VarCharWin WHERE Code LIKE @Prefix;
            END
            """);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Code" && f.Column.TableQualifiedName == "dbo.VarCharWin");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task VarCharColumnLikeNVarcharLiteralPattern_WindowsCollation_StillRangeSeek_OracleConfirmed()
    {
        var report = await Scan("SELECT Code FROM dbo.VarCharWin WHERE Code LIKE N'abc%';");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Code" && f.Column.TableQualifiedName == "dbo.VarCharWin");
        Assert.Equal(Verdict.RangeSeek, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Theory]
    [InlineData(">")]
    [InlineData("<")]
    [InlineData(">=")]
    [InlineData("<=")]
    public async Task VarCharColumnRangeOperatorVsNVarcharVariable_WindowsCollation_StillRangeSeek_OracleConfirmed(string op)
    {
        var report = await Scan($"DECLARE @p NVARCHAR(20); SELECT Code FROM dbo.VarCharWin WHERE Code {op} @p;");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Code" && f.Column.TableQualifiedName == "dbo.VarCharWin");
        Assert.Equal(Verdict.RangeSeek, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task VarCharColumnVsNVarcharColumn_WindowsCollation_JoinPredicate_OnlyOneSideIndexed_StillRangeSeek_OracleConfirmed()
    {
        var report = await Scan("""
            SELECT a.Code
            FROM dbo.VarCharWin a
            JOIN dbo.NVarCharWin b ON a.Code = b.Code;
            """);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Code" && f.Column.TableQualifiedName == "dbo.VarCharWin");
        Assert.Equal(Verdict.RangeSeek, finding.Verdict);
        Assert.True(finding.Column.Indexed);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task VarCharColumnVsNVarcharColumn_WindowsCollation_SameTableWhereClause_StillRangeSeek_OracleConfirmed()
    {
        var report = await Scan("SELECT Code FROM dbo.VarCharWin WHERE Code = OtherCode;");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Code" && f.Column.TableQualifiedName == "dbo.VarCharWin");
        Assert.Equal(Verdict.RangeSeek, finding.Verdict);
        Assert.True(finding.Column.Indexed);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task VarCharColumnOverFourThousandChars_VsNVarcharMaxVariable_WindowsCollation_IsScanForced_OracleConfirmed()
    {
        const string longNarrowDdl = "CREATE TABLE dbo.LongNarrow (Code VARCHAR(4001) COLLATE Latin1_General_CI_AS NOT NULL, INDEX IX_LongCode (Code));";
        var report = await EngineAuthoritativeScan.ScanAsync(
            longNarrowDdl + "\nGO\nDECLARE @p NVARCHAR(MAX); SELECT Code FROM dbo.LongNarrow WHERE Code = @p;",
            "Latin1_General_CI_AS");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Code");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);

        var results = await PipelineOracleVerification.DeployAndVerifyAsync(Options, DatabaseName, longNarrowDdl, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }
}
