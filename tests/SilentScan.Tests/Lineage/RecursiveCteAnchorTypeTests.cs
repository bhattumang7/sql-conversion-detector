using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

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
[Trait("Category", "Oracle")]
public sealed class RecursiveCteAnchorTypeTests
{
    private static async Task<ScanReport> Scan(string sql)
    {
        var report = await EngineAuthoritativeScan.ScanAsync(sql, "SQL_Latin1_General_CP1_CI_AS");
        foreach (var file in report.ParseHealth.Files)
        {
            Assert.Empty(file.Errors);
        }

        return report;
    }

    [Fact]
    public async Task PredicateAgainstRecursiveCteColumn_ClassifiesUsingTheAnchorsType()
    {
        var report = await Scan("""
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
    public async Task RecursionsOwnJoinPredicate_IsNowClassifiable()
    {
        // Bonus unlock: t.CategoryCode (read through the CTE inside the recursive member's own
        // join) used to be Union/Unknown too, making the recursion's own join predicate
        // invisible - a real, common hierarchy-walk implicit-conversion bug. c.ParentCode is
        // varchar; t.CategoryCode now resolves through the anchor's real type, so this predicate
        // (same-type, same-collation) correctly classifies as a non-actionable seek-preserved
        // comparison rather than staying invisible to the summary entirely.
        var report = await Scan("""
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

        // Exactly three comparisons exist in this fixture: the outer WHERE CategoryCode = N'X'
        // (ScanForced, asserted separately above in the sibling test's identical fixture shape),
        // the anchor member's own WHERE ParentCode IS NULL, and the recursion's own join
        // predicate c.ParentCode = t.CategoryCode - the latter two both resolve SeekPreserved
        // (confirmed directly: TypedFindings only lists actionable, non-SeekPreserved predicates,
        // which is why only the ScanForced one appears there). Asserting the full verdict
        // breakdown - not just a >= threshold on the total - pins down that the join predicate
        // specifically resolved to SeekPreserved: if the old Union[BaseColumn, Unknown] wrapper
        // regressed, that predicate would count as Unknown instead (still present in
        // TotalClassified, since Unknown findings are counted too - see TypedPredicateSummary),
        // so UnknownCount would go to 1 and SeekPreservedCount would drop to 1, failing this
        // assertion even though a bare >= 3 threshold on TotalClassified alone would still pass.
        Assert.Equal(3, report.TypedPredicateSummary.TotalClassified);
        Assert.Equal(1, report.TypedPredicateSummary.ScanForcedCount);
        Assert.Equal(2, report.TypedPredicateSummary.SeekPreservedCount);
        Assert.Equal(0, report.TypedPredicateSummary.UnknownCount);
    }

    [Fact]
    public async Task RecursiveCteWithNvarcharJoinMismatch_ClassifiesScanForced()
    {
        // The recursion's own join predicate is now genuinely actionable when there IS a real
        // mismatch - c.ParentCode (varchar) compared against t.CategoryCode (nvarchar, through
        // the CTE) forces the varchar column to convert.
        var report = await Scan("""
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
