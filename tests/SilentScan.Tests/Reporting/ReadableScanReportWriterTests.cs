using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Readable;

namespace SilentScan.Tests.Reporting;

/// <summary>
/// The readable report is the output most readers will ever see, so what it must not do is lose
/// or soften anything the JSON carries: a scan-forced finding has to arrive with its location,
/// its base column, whether that column is indexed and which view layer introduced the mismatch;
/// a seek-preserving comparison must stay out of the findings entirely; and the sections that
/// state what the scan could NOT establish have to survive into it rather than being trimmed as
/// noise.
/// </summary>
public sealed class ReadableScanReportWriterTests
{
    /// <summary>
    /// A varchar column reached through two view layers and compared with an nvarchar parameter
    /// (the column converts, the seek is lost), a seek-preserving comparison in the other
    /// direction, and an unrelated clean predicate to keep the base rate honest.
    /// </summary>
    private const string LayeredSql = """
        CREATE TABLE dbo.Orders (
            OrderId INT NOT NULL PRIMARY KEY,
            OrderCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            Notes NVARCHAR(200) NULL
        );
        GO
        CREATE INDEX IX_Orders_OrderCode ON dbo.Orders (OrderCode);
        GO
        CREATE VIEW dbo.vw_OrdersInner AS SELECT OrderId, OrderCode, Notes FROM dbo.Orders;
        GO
        CREATE VIEW dbo.vw_OrdersOuter AS SELECT OrderId, OrderCode, Notes FROM dbo.vw_OrdersInner;
        GO
        CREATE PROCEDURE dbo.usp_FindOrder @Code NVARCHAR(20), @Notes VARCHAR(200)
        AS
        BEGIN
            SELECT OrderId FROM dbo.vw_OrdersOuter WHERE OrderCode = @Code;
            SELECT OrderId FROM dbo.Orders WHERE Notes = @Notes;
        END
        """;

    private static ScanReport Build(string sql, string? collation = null)
    {
        var parsed = SqlScriptParser.ParseText("shop.sql", sql);
        Assert.False(parsed.HasErrors, string.Join("; ", parsed.Errors.Select(e => e.Message)));
        return ScanReportBuilder.BuildFromParseResults([parsed], collation);
    }

    private static string Render(ScanReport report, CollationSensitivityReport? sensitivity = null) =>
        ReadableScanReportWriter.Write(report, sensitivity, "SilentScan - test", ReadableStyle.Text)
            .ReplaceLineEndings("\n");

    [Fact]
    public void ScanForcedFinding_CarriesLocationColumnIndexedAndTheLayerThatIntroducedIt()
    {
        var report = Build(LayeredSql);
        var rendered = Render(report);

        var row = Assert.Single(
            rendered.Split('\n'),
            line => line.Contains("dbo.Orders.OrderCode", StringComparison.Ordinal) && line.Contains("shop.sql:", StringComparison.Ordinal));

        Assert.Contains("Implicit conversions that force a scan (1)", rendered, StringComparison.Ordinal);
        Assert.Contains("VarChar(20)", row, StringComparison.Ordinal);
        Assert.Contains("NVarChar(20)", row, StringComparison.Ordinal);
        Assert.Contains("yes", row, StringComparison.Ordinal);
        Assert.Contains("2 view layers via dbo.vw_OrdersOuter.OrderCode", row, StringComparison.Ordinal);
    }

    [Fact]
    public void SeekPreservedComparison_IsCountedInTheBaseRateButNeverListed()
    {
        var report = Build(LayeredSql);
        var rendered = Render(report);

        // The nvarchar-column-vs-varchar-value comparison converts the value, not the column.
        Assert.Equal(1, report.TypedPredicateSummary.SeekPreservedCount);
        Assert.DoesNotContain("dbo.Orders.Notes", rendered, StringComparison.Ordinal);
        Assert.Contains("of which 1 keep their seek", rendered, StringComparison.Ordinal);
        Assert.Contains("2 column comparisons classified", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void SectionsWithNothingToReport_AreOmitted()
    {
        var report = Build(LayeredSql);
        var rendered = Render(report);

        Assert.DoesNotContain("Collation conflicts", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Non-sargable predicate patterns", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Files with parse errors", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Dynamic SQL that could not be analyzed", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void UnpinnedCollation_IsStatedEvenWhenEveryCountUnderItIsZero()
    {
        const string NoStringPredicates = """
            CREATE TABLE dbo.T (Id INT NOT NULL);
            GO
            CREATE PROCEDURE dbo.p @Id INT AS SELECT Id FROM dbo.T WHERE Id = @Id;
            """;

        var parsed = SqlScriptParser.ParseText("t.sql", NoStringPredicates);
        var report = ScanReportBuilder.BuildFromParseResults([parsed]);
        var sensitivity = CollationSensitivityReport.Analyze([parsed]);

        var rendered = Render(report, sensitivity);

        Assert.Empty(report.TypedFindings);
        Assert.Contains("No collation was pinned for this scan", rendered, StringComparison.Ordinal);
        Assert.Contains("\"not established\", not \"nothing there\"", rendered, StringComparison.Ordinal);
        Assert.Contains(CollationSensitivityReport.DefaultWindowsFamilyCollation, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseFailures_AreListedWithTheirFirstError()
    {
        var parsed = SqlScriptParser.ParseText("broken.sql", "SELECT FROM WHERE ORDER;");
        Assert.True(parsed.HasErrors);
        var report = ScanReportBuilder.BuildFromParseResults([parsed]);

        var rendered = Render(report);

        Assert.Contains("Files with parse errors (1)", rendered, StringComparison.Ordinal);
        Assert.Contains("broken.sql", rendered, StringComparison.Ordinal);
        Assert.Contains("line 1:", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void NonSargablePattern_IsExplainedOncePerPatternRatherThanPerRow()
    {
        const string Sql = """
            CREATE TABLE dbo.Users (Id INT NOT NULL, CreatedAt DATETIME NOT NULL, UpdatedAt DATETIME NOT NULL);
            GO
            CREATE PROCEDURE dbo.p @Year INT AS
            SELECT Id FROM dbo.Users WHERE YEAR(CreatedAt) = @Year AND YEAR(UpdatedAt) = @Year;
            """;

        var rendered = Render(Build(Sql));

        Assert.Contains("Column wrapped in a function (2)", rendered, StringComparison.Ordinal);
        var explanation = "The index stores the column's values, not the function's results";
        Assert.Equal(1, CountOccurrences(rendered, explanation));
        Assert.Contains("CreatedAt", rendered, StringComparison.Ordinal);
        Assert.Contains("UpdatedAt", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownAndText_ReportTheSameFindings()
    {
        var report = Build(LayeredSql);
        var sensitivity = CollationSensitivityReport.Analyze([SqlScriptParser.ParseText("shop.sql", LayeredSql)]);

        var text = ReadableScanReportWriter.Write(report, sensitivity, "t", ReadableStyle.Text);
        var markdown = ReadableScanReportWriter.Write(report, sensitivity, "t", ReadableStyle.Markdown);

        foreach (var expected in new[] { "dbo.Orders.OrderCode", "2 view layers via dbo.vw_OrdersOuter.OrderCode", "No collation was pinned for this scan" })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
            Assert.Contains(expected, markdown, StringComparison.Ordinal);
        }

        Assert.Contains("# t", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("# t", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FindingPaths_AreShownRelativeToTheScanRoot()
    {
        var parsed = SqlScriptParser.ParseText(Path.Combine("/repo", "sql", "shop.sql"), LayeredSql);
        var report = ScanReportBuilder.BuildFromParseResults([parsed], "SQL_Latin1_General_CP1_CI_AS");

        var rendered = ReadableScanReportWriter.Write(report, null, "t", ReadableStyle.Text, "/repo");

        Assert.Contains(Path.Combine("sql", "shop.sql") + ":", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("/repo/sql", rendered.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public void ScanRootThatIsOnlyATextualPrefix_IsNotTrimmed()
    {
        var parsed = SqlScriptParser.ParseText("/src/application/shop.sql", LayeredSql);
        var report = ScanReportBuilder.BuildFromParseResults([parsed], "SQL_Latin1_General_CP1_CI_AS");

        var rendered = ReadableScanReportWriter.Write(report, null, "t", ReadableStyle.Text, "/src/app");

        // The whole path survives - trimming a prefix that stops mid-segment would leave
        // "lication/shop.sql", a path that points at nothing.
        Assert.Contains("/src/application/shop.sql:", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("  lication/shop.sql", rendered, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
