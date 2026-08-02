using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Readable;

namespace SilentScan.Tests.Reporting;

/// <summary>
/// The corpus report's rollup table is what answers "which repo do I look at" without reading
/// five reports first, so it has to carry the three facts that decide that: how many findings,
/// whether the repo's files actually parsed as T-SQL, and whether its collation was pinned at
/// all. A repo below the parse-success bar must be visibly marked rather than sitting in the
/// table looking like any other row.
/// </summary>
public sealed class ReadableCorpusReportWriterTests
{
    private const string FindingSql = """
        CREATE TABLE dbo.Users (DisplayName VARCHAR(40) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
        GO
        CREATE PROCEDURE dbo.usp_FindUser @DisplayName NVARCHAR(40)
        AS SELECT DisplayName FROM dbo.Users WHERE DisplayName = @DisplayName;
        """;

    private static ReadableCorpusRepo Repo(string name, string sql, string? collation = "SQL_Latin1_General_CP1_CI_AS", string path = "src/x.sql")
    {
        var parsed = SqlScriptParser.ParseText(path, sql);
        return new ReadableCorpusRepo(name, ScanReportBuilder.BuildFromParseResults([parsed], collation), null);
    }

    [Fact]
    public void RollupTable_HasOneRowPerRepoWithItsFindingsParseRateAndCollationState()
    {
        var unpinnedParse = SqlScriptParser.ParseText("y.sql", FindingSql);
        var repos = new[]
        {
            Repo("alpha", FindingSql),
            new ReadableCorpusRepo(
                "beta",
                ScanReportBuilder.BuildFromParseResults([unpinnedParse]),
                CollationSensitivityReport.Analyze([unpinnedParse])),
        };

        var rendered = ReadableCorpusReportWriter.Write(repos, [], ReadableStyle.Text).ReplaceLineEndings("\n");
        var lines = rendered.Split('\n');

        var alpha = Assert.Single(lines, line => line.StartsWith("  alpha", StringComparison.Ordinal));
        var beta = Assert.Single(lines, line => line.StartsWith("  beta", StringComparison.Ordinal));

        Assert.Contains("pass", alpha, StringComparison.Ordinal);
        Assert.Contains("pinned", alpha, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT PINNED", alpha, StringComparison.Ordinal);
        Assert.Contains("NOT PINNED", beta, StringComparison.Ordinal);

        // Each repo's own full report follows the table.
        Assert.Contains("dbo.Users.DisplayName", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RepoBelowTheDialectSniffingBar_IsMarkedAndNamed()
    {
        var broken = SqlScriptParser.ParseText("broken.sql", "SELECT FROM WHERE ORDER;");
        var repo = new ReadableCorpusRepo("mysql-ish", ScanReportBuilder.BuildFromParseResults([broken]), null);

        var rendered = ReadableCorpusReportWriter.Write([repo], [], ReadableStyle.Text);

        Assert.Contains("BELOW BAR", rendered, StringComparison.Ordinal);
        Assert.Contains("parse-success bar the corpus uses", rendered, StringComparison.Ordinal);
        Assert.Contains("mysql-ish", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ReposWithoutAClone_AreNamedRatherThanSilentlyAbsent()
    {
        var rendered = ReadableCorpusReportWriter.Write([Repo("alpha", FindingSql)], ["gamma"], ReadableStyle.Text);

        Assert.Contains("no local clone was found", rendered, StringComparison.Ordinal);
        Assert.Contains("- gamma", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void PerRepoSections_SitUnderTheRepoHeadingRatherThanOutrankingIt()
    {
        var rendered = ReadableCorpusReportWriter.Write([Repo("alpha", FindingSql)], [], ReadableStyle.Markdown);

        Assert.Contains("# SilentScan corpus scan", rendered, StringComparison.Ordinal);
        Assert.Contains("## alpha", rendered, StringComparison.Ordinal);
        Assert.Contains("### Summary", rendered, StringComparison.Ordinal);
    }
}
