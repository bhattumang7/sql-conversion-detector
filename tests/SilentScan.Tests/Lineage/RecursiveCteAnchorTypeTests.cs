using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Lineage;

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

        var finding = Assert.Single(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"), f => f.Column.ColumnName == "CategoryCode" && f.Column.TableQualifiedName == "Tree");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);

        Assert.False(finding.Column.Indexed);
    }

    [Fact]
    public async Task RecursionsOwnJoinPredicate_IsNowClassifiable()
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

        Assert.Equal(3, report.TypedPredicateSummary.TotalClassified);
        Assert.Equal(1, report.TypedPredicateSummary.ScanForcedCount);
        Assert.Equal(2, report.TypedPredicateSummary.SeekPreservedCount);
        Assert.Equal(0, report.TypedPredicateSummary.UnknownCount);
    }

    [Fact]
    public async Task RecursiveCteWithNvarcharJoinMismatch_ClassifiesScanForced()
    {

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

        var finding = Assert.Single(report.Find<TypedPredicateFinding>("TypedPredicateExtractor"), f => f.Column.ColumnName == "ParentCode");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
    }
}
