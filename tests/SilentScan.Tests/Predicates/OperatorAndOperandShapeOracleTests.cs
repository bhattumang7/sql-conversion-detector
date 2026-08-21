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
/// Column-vs-column (same table AND joined) was investigated TWICE as a second correction and
/// rejected both times - see VerdictClassifier.Classify's own remarks for the full history,
/// including why a real-data-driven probe (which DID show a same-table comparison losing the
/// seek) was the wrong signal to trust: this project's own oracle harness checks compiled-plan
/// construct presence, never a specific data volume's plan CHOICE, precisely because a verdict
/// must never depend on the cardinality estimator.
/// </summary>
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
        // Matches the matrix's own probed pair/collation exactly - only the operator (LIKE
        // against a non-literal pattern) and predicate shape differ from what was probed (`=`).
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
        // Near-miss: a LIKE pattern that IS a literal keeps the dynamic range seek, agreeing
        // with the matrix's own `=`-probed outcome for this pair/collation.
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
        // Pins that the matrix's `=`-probed RangeSeek outcome DOES generalize to every range
        // operator for this pair/collation (oracle-verified directly across >, <, >=, <=) - only
        // LIKE-with-a-non-literal-pattern diverges. Guards against a future change assuming
        // operator-invariance is universal rather than checking it.
        var report = await Scan($"DECLARE @p NVARCHAR(20); SELECT Code FROM dbo.VarCharWin WHERE Code {op} @p;");

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
        // Same category pair and collation as the JOIN test above, but both columns come from
        // the SAME row (a plain WHERE-clause comparison, not a join predicate). A real-data probe
        // (5,000 rows, real statistics) shows the optimizer choosing NOT to use the dynamic range
        // seek here - but that is a cardinality-driven plan CHOICE, not a structural fact, and
        // this project's own oracle harness (DDL-only, checks compiled-plan construct presence)
        // confirms GetRangeThroughConvert is still structurally present. RangeSeek is correct here
        // by the same reasoning as the JOIN case above. Guards against reintroducing a same-table
        // correction based on a real-data probe rather than this project's calibrated methodology.
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
        // A non-unicode column longer than 4000 characters compared against a unicode MAX value
        // is promoted to unicode MAX by the engine before the seek is considered
        // (VerdictClassifier.IsLengthTriggeredUnicodePromotion) - loses the dynamic range seek
        // the same category pair gets at a bounded length (see the LIKE/range-operator tests
        // above, all VARCHAR(20)). Unlike the column-vs-column correction this class's own
        // remarks reject, this one IS a structural fact: this project's own DDL-only oracle
        // harness confirms GetRangeThroughConvert is genuinely absent from the compiled plan
        // here, not merely a real-data-driven plan choice.
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
