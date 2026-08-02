using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Lineage;

/// <summary>
/// Regression coverage for the recursive CTE anchor-type gap (formerly pinned in
/// KnownGapCharacterizationTests.RecursiveCte_RecursiveBranchIsUnknown_MismatchThroughCteNeverConfirmed):
/// CteResolver.ResolveRecursiveAnchor used to wrap the anchor's own provenance in
/// Union[BaseColumn, Unknown] to represent the unresolved recursive member - but T-SQL enforces
/// (Msg 240) that the recursive member's column types match the anchor's exactly, so that
/// wrapper made every predicate through any recursive CTE unclassifiable for no real reason.
/// Runs through <see cref="ScanReportBuilder"/>, the same entry point production uses.
/// </summary>
public sealed class RecursiveCteAnchorTypeTests
{
    private static ScanReport Scan(string sql)
    {
        var parseResult = SqlScriptParser.ParseText("recursive_cte.sql", sql);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");
        foreach (var file in report.ParseHealth.Files)
        {
            Assert.Empty(file.Errors);
        }

        return report;
    }

    [Fact]
    public void PredicateAgainstRecursiveCteColumn_ClassifiesUsingTheAnchorsType()
    {
        var report = Scan("""
            CREATE TABLE dbo.Categories (
                CategoryCode varchar(20) NOT NULL,
                ParentCode varchar(20) NULL,
                INDEX IX_CategoryCode (CategoryCode));
            GO
            WITH Tree AS (
                SELECT CategoryCode, ParentCode FROM dbo.Categories WHERE ParentCode IS NULL
                UNION ALL
                SELECT c.CategoryCode, c.ParentCode
                FROM dbo.Categories c
                INNER JOIN Tree t ON c.ParentCode = t.CategoryCode)
            SELECT 1 FROM Tree WHERE CategoryCode = N'X';
            """);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "CategoryCode" && f.Column.TableQualifiedName == "Tree");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        // The index claim is deliberately dropped - a recursive CTE materializes through a
        // spool, not a direct index access, so this must never claim Indexed=true.
        Assert.False(finding.Column.Indexed);
    }

    [Fact]
    public void RecursionsOwnJoinPredicate_IsNowClassifiable()
    {
        // Bonus unlock: t.CategoryCode (read through the CTE inside the recursive member's own
        // join) used to be Union/Unknown too, making the recursion's own join predicate
        // invisible - a real, common hierarchy-walk implicit-conversion bug. c.ParentCode is
        // varchar; t.CategoryCode now resolves through the anchor's real type, so this predicate
        // (same-type, same-collation) correctly classifies as a non-actionable seek-preserved
        // comparison rather than staying invisible to the summary entirely.
        var report = Scan("""
            CREATE TABLE dbo.Categories (
                CategoryCode varchar(20) NOT NULL,
                ParentCode varchar(20) NULL,
                INDEX IX_CategoryCode (CategoryCode));
            GO
            WITH Tree AS (
                SELECT CategoryCode, ParentCode FROM dbo.Categories WHERE ParentCode IS NULL
                UNION ALL
                SELECT c.CategoryCode, c.ParentCode
                FROM dbo.Categories c
                INNER JOIN Tree t ON c.ParentCode = t.CategoryCode)
            SELECT 1 FROM Tree WHERE CategoryCode = N'X';
            """);

        Assert.True(report.TypedPredicateSummary.TotalClassified >= 2);
    }

    [Fact]
    public void RecursiveCteWithNvarcharJoinMismatch_ClassifiesScanForced()
    {
        // The recursion's own join predicate is now genuinely actionable when there IS a real
        // mismatch - c.ParentCode (varchar) compared against t.CategoryCode (nvarchar, through
        // the CTE) forces the varchar column to convert.
        var report = Scan("""
            CREATE TABLE dbo.Categories (
                CategoryCode nvarchar(20) NOT NULL,
                ParentCode varchar(20) NULL,
                INDEX IX_ParentCode (ParentCode));
            GO
            WITH Tree AS (
                SELECT CategoryCode, ParentCode FROM dbo.Categories WHERE ParentCode IS NULL
                UNION ALL
                SELECT c.CategoryCode, c.ParentCode
                FROM dbo.Categories c
                INNER JOIN Tree t ON c.ParentCode = t.CategoryCode)
            SELECT 1 FROM Tree WHERE CategoryCode = N'X';
            """);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "ParentCode");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }
}
