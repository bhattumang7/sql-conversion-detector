using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// The type-pair matrix's RangeSeek cells were all originally probed as a column compared to a
/// DECLARE'd variable under `=`. Oracle-verified directly (Docker SQL Server) that this probe
/// shape does not fully generalize: a LIKE predicate whose pattern is not a literal loses the
/// dynamic range seek and is genuinely ScanForced instead of RangeSeek - a real misclassification
/// this suite closes (see VerdictClassifier.Classify's operatorText parameter).
///
/// Column-vs-column (a JOIN predicate) was investigated as a second correction and deliberately
/// NOT made: an initial sample showed the same pattern, but further probing found it confounded -
/// whether the dynamic range seek disappears depends on whether the OTHER column is ALSO
/// indexed, not on the type pair alone. That's a plan-shape-dependent fact CLAUDE.md's own oracle
/// discipline warns against trusting, not a stable one the matrix could safely encode - a blanket
/// correction would misclassify the far more common single-indexed-side join as ScanForced when
/// it is genuinely RangeSeek. The test below pins today's (correct, matrix-driven) RangeSeek
/// answer for that common shape so a future change can't silently reintroduce the confounded
/// "always ScanForced" version of this correction.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class OperatorAndOperandShapeOracleTests : OracleTestFixture
{
    private const string Ddl_ = """
        CREATE TABLE dbo.VarCharWin (Code VARCHAR(20) COLLATE Latin1_General_CI_AS NOT NULL, INDEX IX_Code (Code));
        GO
        CREATE TABLE dbo.NVarCharWin (Code NVARCHAR(20) COLLATE Latin1_General_CI_AS NOT NULL);
        """;

    protected override string DatabaseNameSeed => nameof(OperatorAndOperandShapeOracleTests);

    protected override string Ddl => Ddl_;

    private static ScanReport Scan(string sql)
    {
        var parseResult = SqlScriptParser.ParseText("op.sql", Ddl_ + "\nGO\n" + sql);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "Latin1_General_CI_AS");
        foreach (var file in report.ParseHealth.Files)
        {
            Assert.Empty(file.Errors);
        }

        return report;
    }

    [Fact]
    public async Task VarCharColumnLikeNVarcharVariable_WindowsCollation_IsScanForced_OracleConfirmed()
    {
        // Matches the matrix's own probed pair/collation exactly - only the operator (LIKE
        // against a non-literal pattern) and predicate shape differ from what was probed (`=`).
        var report = Scan("""
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
        // Near-miss: a LIKE pattern that IS a literal keeps the dynamic range seek, agreeing
        // with the matrix's own `=`-probed outcome for this pair/collation.
        var report = Scan("SELECT Code FROM dbo.VarCharWin WHERE Code LIKE N'abc%';");

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
        // Pins that the matrix's `=`-probed RangeSeek outcome DOES generalize to every range
        // operator for this pair/collation (oracle-verified directly across >, <, >=, <=) - only
        // LIKE-with-a-non-literal-pattern diverges. Guards against a future change assuming
        // operator-invariance is universal rather than checking it.
        var report = Scan($"DECLARE @p NVARCHAR(20); SELECT Code FROM dbo.VarCharWin WHERE Code {op} @p;");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Code" && f.Column.TableQualifiedName == "dbo.VarCharWin");
        Assert.Equal(Verdict.RangeSeek, finding.Verdict);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task VarCharColumnVsNVarcharColumn_WindowsCollation_JoinPredicate_OnlyOneSideIndexed_StillRangeSeek_OracleConfirmed()
    {
        // Pins the common real-world shape (only the classified side indexed - dbo.NVarCharWin
        // above has no index at all) against the confounded "always ScanForced" correction this
        // class's own doc comment explains was deliberately NOT made. If a future change
        // reintroduces a blanket column-vs-column downgrade, this regresses loudly.
        var report = Scan("""
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
}
