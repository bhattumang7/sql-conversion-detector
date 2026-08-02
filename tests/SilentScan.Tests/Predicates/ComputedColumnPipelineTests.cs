using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Regression coverage for the computed-column type inference gap (formerly pinned in
/// KnownGapCharacterizationTests.ComputedColumn_TypeIsNeverInferred_PredicateIsSilentlyDropped):
/// a predicate against a computed column whose defining expression is trivially inferable
/// (sibling column references, literals, CAST/CONVERT, binary expressions) must classify
/// normally instead of the comparison vanishing with no finding, no Unknown, and no
/// skip-ledger entry. Runs through <see cref="ScanReportBuilder"/>, the same entry point
/// production uses.
/// </summary>
public sealed class ComputedColumnPipelineTests
{
    [Fact]
    public void StringConcatenationComputedColumn_AgainstNvarcharLiteral_ClassifiesScanForced()
    {
        var parseResult = SqlScriptParser.ParseText("computed_column.sql", """
            CREATE TABLE dbo.People (
                FirstName varchar(40) NOT NULL,
                LastName varchar(40) NOT NULL,
                FullName AS (FirstName + ' ' + LastName));
            GO
            SELECT 1 FROM dbo.People WHERE FullName = N'John Smith';
            """);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "FullName");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }

    [Fact]
    public void ComputedColumnWithUnresolvableExpression_StillReachesTheReport()
    {
        // A function-wrapped computed column expression stays Unknown (out of the resolver's
        // narrow scope) but must still surface: either as a classified Unknown comparison or a
        // skip-ledger entry - never a comparison that disappears with zero trace.
        var parseResult = SqlScriptParser.ParseText("computed_column_unresolvable.sql", """
            CREATE TABLE dbo.T (
                Created datetime NOT NULL,
                CreatedYear AS (YEAR(Created)));
            GO
            SELECT 1 FROM dbo.T WHERE CreatedYear = 2024;
            """);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");

        var hasUnknownFinding = report.TypedFindings.Any(f => f.Column.ColumnName == "CreatedYear" && f.Verdict == Verdict.Unknown);
        var hasSkipEntry = report.SkippedConstructs.Any(s => s.ConstructKind == "computed column type" && s.Reason.Contains("CreatedYear", StringComparison.Ordinal));

        Assert.True(hasUnknownFinding || hasSkipEntry);
    }
}
