using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Readable;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Reporting;

/// <summary>
/// The readable report is the output most readers will ever see, so what it must not do is lose
/// or soften anything the JSON carries: a scan-forced finding has to arrive with its location,
/// its base column, whether that column is indexed and which view layer introduced the mismatch;
/// a seek-preserving comparison must stay out of the findings entirely; and the sections that
/// state what the scan could NOT establish have to survive into it rather than being trimmed as
/// noise.
/// </summary>
[Trait("Category", "Oracle")]
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

    private static Task<ScanReport> Build(string sql, string? collation = null) =>
        EngineAuthoritativeScan.ScanAsync(sql, collation);

    private static string Render(ScanReport report) =>
        ReadableScanReportWriter.Write(report, "SilentScan - test", ReadableStyle.Text, verbosity: ReadableVerbosity.Full)
            .ReplaceLineEndings("\n");

    [Fact]
    public async Task ScanForcedFinding_CarriesLocationColumnIndexedAndTheLayerThatIntroducedIt()
    {
        var report = await Build(LayeredSql);
        var rendered = Render(report);

        var row = Assert.Single(
            rendered.Split('\n'),
            line => line.Contains("dbo.Orders.OrderCode", StringComparison.Ordinal) && line.Contains("dbo.usp_FindOrder:", StringComparison.Ordinal));

        Assert.Contains("Implicit conversions that force a scan (1)", rendered, StringComparison.Ordinal);
        Assert.Contains("VarChar(20)", row, StringComparison.Ordinal);
        Assert.Contains("NVarChar(20)", row, StringComparison.Ordinal);
        Assert.Contains("yes", row, StringComparison.Ordinal);
        Assert.Contains("2 view layers via dbo.vw_OrdersOuter.OrderCode", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeekPreservedComparison_IsCountedInTheBaseRateButNeverListed()
    {
        var report = await Build(LayeredSql);
        var rendered = Render(report);

        // The nvarchar-column-vs-varchar-value comparison converts the value, not the column.
        Assert.Equal(1, report.TypedPredicateSummary.SeekPreservedCount);
        Assert.DoesNotContain("dbo.Orders.Notes", rendered, StringComparison.Ordinal);
        Assert.Contains("of which 1 keep their seek", rendered, StringComparison.Ordinal);
        Assert.Contains("2 column comparisons classified", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SectionsWithNothingToReport_AreOmitted()
    {
        var report = await Build(LayeredSql);
        var rendered = Render(report);

        Assert.DoesNotContain("Collation conflicts", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Non-sargable predicate patterns", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Files with parse errors", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Dynamic SQL that could not be analyzed", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseFailures_AreListedWithTheirFirstError()
    {
        var parsed = SqlScriptParser.ParseText("broken.sql", "SELECT FROM WHERE ORDER;");
        Assert.True(parsed.HasErrors);
        var report = ScanReportBuilder.BuildFromParseResults([parsed], new DatabaseCatalog());

        var rendered = Render(report);

        Assert.Contains("Files with parse errors (1)", rendered, StringComparison.Ordinal);
        Assert.Contains("broken.sql", rendered, StringComparison.Ordinal);
        Assert.Contains("line 1:", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultVerbosity_IsBrief_ButNeverGatesARealFinding()
    {
        var report = await Build(LayeredSql);

        // No verbosity argument - the real default. A ScanForced finding is the entire point of
        // this tool, so brief mode must never touch it, unlike the coverage/caveat sections.
        var rendered = ReadableScanReportWriter.Write(report, "SilentScan - test", ReadableStyle.Text)
            .ReplaceLineEndings("\n");

        var row = Assert.Single(
            rendered.Split('\n'),
            line => line.Contains("dbo.Orders.OrderCode", StringComparison.Ordinal) && line.Contains("dbo.usp_FindOrder:", StringComparison.Ordinal));

        Assert.Contains("Implicit conversions that force a scan (1)", rendered, StringComparison.Ordinal);
        Assert.Contains("2 view layers via dbo.vw_OrdersOuter.OrderCode", row, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultVerbosity_IsBrief_ParseFailuresStateCountWithoutPerFileDetail()
    {
        var parsed = SqlScriptParser.ParseText("broken.sql", "SELECT FROM WHERE ORDER;");
        Assert.True(parsed.HasErrors);
        var report = ScanReportBuilder.BuildFromParseResults([parsed], new DatabaseCatalog());

        // No verbosity argument at all - exercises the real default a caller who never heard of
        // the flag gets, not an explicit Brief request.
        var rendered = ReadableScanReportWriter.Write(report, "SilentScan - test", ReadableStyle.Text)
            .ReplaceLineEndings("\n");

        Assert.Contains("Files with parse errors (1)", rendered, StringComparison.Ordinal);
        Assert.Contains("re-run with --verbosity full", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("broken.sql", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("line 1:", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FullVerbosity_RestoresParseFailurePerFileDetail()
    {
        var parsed = SqlScriptParser.ParseText("broken.sql", "SELECT FROM WHERE ORDER;");
        var report = ScanReportBuilder.BuildFromParseResults([parsed], new DatabaseCatalog());

        var rendered = ReadableScanReportWriter.Write(report, "SilentScan - test", ReadableStyle.Text, verbosity: ReadableVerbosity.Full)
            .ReplaceLineEndings("\n");

        Assert.Contains("broken.sql", rendered, StringComparison.Ordinal);
        Assert.Contains("line 1:", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("re-run with --verbosity full", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonSargablePattern_IsExplainedOncePerPatternRatherThanPerRow()
    {
        const string Sql = """
            CREATE TABLE dbo.Users (Id INT NOT NULL, CreatedAt DATETIME NOT NULL, UpdatedAt DATETIME NOT NULL);
            GO
            CREATE PROCEDURE dbo.p @Year INT AS
            SELECT Id FROM dbo.Users WHERE YEAR(CreatedAt) = @Year AND YEAR(UpdatedAt) = @Year;
            """;

        var rendered = Render(await Build(Sql));

        Assert.Contains("Date-part function applied to the column (2)", rendered, StringComparison.Ordinal);
        var explanation = "Oracle-verified: the date-part function forces a per-row scan";
        Assert.Equal(1, CountOccurrences(rendered, explanation));
        Assert.Contains("CreatedAt", rendered, StringComparison.Ordinal);
        Assert.Contains("UpdatedAt", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkdownAndText_ReportTheSameFindings()
    {
        var report = await Build(LayeredSql);

        var text = ReadableScanReportWriter.Write(report, "t", ReadableStyle.Text);
        var markdown = ReadableScanReportWriter.Write(report, "t", ReadableStyle.Markdown);

        foreach (var expected in new[] { "dbo.Orders.OrderCode", "2 view layers via dbo.vw_OrdersOuter.OrderCode" })
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
        var report = ReportWithFindingAt(Path.Combine("/repo", "sql", "shop.sql"));

        var rendered = ReadableScanReportWriter.Write(report, "t", ReadableStyle.Text, "/repo");

        Assert.Contains(Path.Combine("sql", "shop.sql") + ":", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("/repo/sql", rendered.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public void ScanRootThatIsOnlyATextualPrefix_IsNotTrimmed()
    {
        var report = ReportWithFindingAt("/src/application/shop.sql");

        var rendered = ReadableScanReportWriter.Write(report, "t", ReadableStyle.Text, "/src/app");

        // The whole path survives - trimming a prefix that stops mid-segment would leave
        // "lication/shop.sql", a path that points at nothing.
        Assert.Contains("/src/application/shop.sql:", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("  lication/shop.sql", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpressionDerivedFindings_TheOnesWithARealIndexUnderneathComeFirst()
    {
        // Both findings sort later than each other by SourcePath alone (b before a) - if the
        // section were still just SourcePath/Line ordered, "b.sql" (no index underneath) would
        // print first. Indexed-first must override that.
        var indexed = new ExpressionDerivedFinding(
            "Col", "a.sql", 10, 1, [], [new UnderlyingBaseColumn("dbo.T1", "Col1", Indexed: true)]);
        var notIndexed = new ExpressionDerivedFinding(
            "Col", "b.sql", 5, 1, [], [new UnderlyingBaseColumn("dbo.T2", "Col2", Indexed: false)]);

        var report = new ScanReport(
            new ParseHealthReport([]), [], [], [], [notIndexed, indexed], [], [],
            [], [], [], [], [], [], [], [], [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            SkippedConstructSummary.From([]), TypedPredicateSummary.From([]), DynamicSqlSummary.From([]));

        var rendered = ReadableScanReportWriter.Write(report, "t", ReadableStyle.Text);

        Assert.True(
            rendered.IndexOf("a.sql:10", StringComparison.Ordinal) < rendered.IndexOf("b.sql:5", StringComparison.Ordinal),
            "the finding with a real index underneath its expression must print before the one with none, regardless of source order");
    }

    /// <summary>
    /// A minimal, hand-built report carrying one ScanForced finding at <paramref name="sourcePath"/>
    /// - the path-trimming behavior under test lives entirely in
    /// <see cref="ReadableScanReportWriter"/>'s own presentation logic over whatever SourcePath a
    /// finding carries, not in how that finding's type was inferred, so there is nothing gained by
    /// routing it through a real deployment (whose live-mode source paths are module qualified
    /// names, not file paths, and could never carry an arbitrary path like "/repo/sql/shop.sql" in
    /// the first place).
    /// </summary>
    private static ScanReport ReportWithFindingAt(string sourcePath) => new(
        new ParseHealthReport([]),
            [],
        [new TypedPredicateFinding(
            Verdict.ScanForced,
            new PredicateOperand.Column("dbo.T", "Col", new SqlType(SqlTypeCategory.VarChar), Indexed: true, Depth: 0, Provenance: null!),
            new PredicateOperand.Value(null),
            "=",
            sourcePath,
            1,
            1)],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
            [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
            [],
            [],
            [],
        [],
        [],
        [],
        [],
        [],
        [],
        SkippedConstructSummary.From([]),
        TypedPredicateSummary.From([]),
        DynamicSqlSummary.From([]));

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
