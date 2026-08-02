using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Regression coverage for the computed-column type inference gap (formerly pinned in
/// KnownGapCharacterizationTests.ComputedColumn_TypeIsNeverInferred_PredicateIsSilentlyDropped):
/// a predicate against a computed column whose defining expression is trivially inferable
/// (sibling column references, literals, CAST/CONVERT, binary expressions) must classify
/// normally instead of the comparison vanishing with no finding, no Unknown, and no
/// skip-ledger entry. Runs through <see cref="ScanReportBuilder"/>, the same entry point
/// production uses - and, for the verdict-bearing case, against the real SQL Server oracle
/// (CLAUDE.md: verify the real thing, not just that the static pipeline agrees with itself).
/// </summary>
[Trait("Category", "Oracle")]
public sealed class ComputedColumnPipelineTests : OracleTestFixture
{
    private const string ConcatSql = """
        CREATE TABLE dbo.People (
            FirstName varchar(40) NOT NULL,
            LastName varchar(40) NOT NULL,
            FullName AS (FirstName + ' ' + LastName));
        GO
        SELECT 1 FROM dbo.People WHERE FullName = N'John Smith';
        """;

    private const string UnresolvableSql = """
        CREATE TABLE dbo.T (
            Created datetime NOT NULL,
            CreatedYear AS (YEAR(Created)));
        GO
        SELECT 1 FROM dbo.T WHERE CreatedYear = 2024;
        """;

    protected override string DatabaseName => nameof(ComputedColumnPipelineTests);

    protected override string Ddl => ConcatSql + "\nGO\n" + UnresolvableSql;

    [Fact]
    public async Task StringConcatenationComputedColumn_AgainstNvarcharLiteral_ClassifiesScanForced_OracleConfirmed()
    {
        var parseResult = SqlScriptParser.ParseText("computed_column.sql", ConcatSql);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "FullName");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        // FullName is NOT persisted, so SQL Server inlines its defining expression at compile
        // time rather than materializing a column named FullName - the plan's ColumnReference
        // nodes only ever name the real underlying columns the expression was built from
        // (discovered running this test against the live oracle: the generic exact-name
        // confirmation in PipelineOracleVerification, which works for every ordinary column,
        // reported NotConfirmed here because "FullName" never appears in the plan at all). The
        // real, oracle-visible signal for a non-persisted computed column's ScanForced verdict
        // is that CONVERT_IMPLICIT lands on one of its constituent base columns instead.
        var probe = "SELECT 1 FROM dbo.People WHERE FullName = N'John Smith';";
        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);
        var conversions = SilentScan.Verify.Oracle.ConvertImplicitDetector.FindColumnConversions(planXml);
        Assert.Contains(conversions, c =>
            string.Equals(c.Table, "People", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(c.Column, "FirstName", StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Column, "LastName", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ComputedColumnWithUnresolvableExpression_StillReachesTheReport()
    {
        // A function-wrapped computed column expression stays Unknown (out of the resolver's
        // narrow scope) but must still surface: either as a classified Unknown comparison or a
        // skip-ledger entry - never a comparison that disappears with zero trace. Unknown makes
        // no claim about what the engine actually does, so there is nothing to oracle-confirm
        // here (CLAUDE.md: Unknown means honestly uncertain, never a guess to be double-checked).
        var parseResult = SqlScriptParser.ParseText("computed_column_unresolvable.sql", UnresolvableSql);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");

        var hasUnknownFinding = report.TypedFindings.Any(f => f.Column.ColumnName == "CreatedYear" && f.Verdict == Verdict.Unknown);
        var hasSkipEntry = report.SkippedConstructs.Any(s => s.ConstructKind == "computed column type" && s.Reason.Contains("CreatedYear", StringComparison.Ordinal));

        Assert.True(hasUnknownFinding || hasSkipEntry);
    }
}
