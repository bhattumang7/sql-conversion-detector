using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Readable;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Reporting;

[Trait("Category", "Oracle")]
public sealed class ReadableCorpusReportWriterTests
{
    private const string FindingSql = """
        CREATE TABLE dbo.Users (DisplayName VARCHAR(40) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
        GO
        CREATE PROCEDURE dbo.usp_FindUser @DisplayName NVARCHAR(40)
        AS SELECT DisplayName FROM dbo.Users WHERE DisplayName = @DisplayName;
        """;

    private static async Task<ReadableCorpusRepo> Repo(string name, string sql, string? collation = "SQL_Latin1_General_CP1_CI_AS")
    {
        var report = await EngineAuthoritativeScan.ScanAsync(sql, collation);
        return new ReadableCorpusRepo(name, report, null);
    }

    [Fact]
    public async Task RollupTable_HasOneRowPerRepoWithItsFindingsAndParseRate()
    {
        var repos = new[] { await Repo("alpha", FindingSql) };

        var rendered = ReadableCorpusReportWriter.Write(repos, [], ReadableStyle.Text).ReplaceLineEndings("\n");
        var lines = rendered.Split('\n');

        var alpha = Assert.Single(lines, line => line.StartsWith("  alpha", StringComparison.Ordinal));

        var expectedParseRate =
            $"{(repos[0].Report.ParseHealth.ParseSuccessRate * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}%";
        Assert.Contains(expectedParseRate, alpha, StringComparison.Ordinal);
        Assert.Contains("pass", alpha, StringComparison.Ordinal);

        var sectionStart = Array.IndexOf(lines, "alpha");
        Assert.True(sectionStart >= 0, "expected a per-repo heading line for alpha");
        var alphaSection = string.Join('\n', lines.Skip(sectionStart));
        Assert.Contains("dbo.Users.DisplayName", alphaSection, StringComparison.Ordinal);
    }

    [Fact]
    public void RepoBelowTheDialectSniffingBar_IsMarkedAndNamed()
    {

        var broken = SqlScriptParser.ParseText("broken.sql", "SELECT FROM WHERE ORDER;");
        var report = ScanReportBuilder.BuildFromParseResults([broken], new DatabaseCatalog());
        var repo = new ReadableCorpusRepo("mysql-ish", report, null);

        var rendered = ReadableCorpusReportWriter.Write([repo], [], ReadableStyle.Text).ReplaceLineEndings("\n");
        var lines = rendered.Split('\n');

        var row = Assert.Single(lines, line => line.StartsWith("  mysql-ish", StringComparison.Ordinal));
        Assert.Contains("BELOW BAR", row, StringComparison.Ordinal);

        var barParagraph = Assert.Single(lines, line => line.Contains("parse-success bar the corpus uses", StringComparison.Ordinal));
        Assert.Contains("mysql-ish", barParagraph, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReposWithoutAClone_AreNamedRatherThanSilentlyAbsent()
    {
        var rendered = ReadableCorpusReportWriter.Write([await Repo("alpha", FindingSql)], ["gamma"], ReadableStyle.Text);

        Assert.Contains("no local clone was found", rendered, StringComparison.Ordinal);
        Assert.Contains("- gamma", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PerRepoSections_SitUnderTheRepoHeadingRatherThanOutrankingIt()
    {
        var rendered = ReadableCorpusReportWriter.Write([await Repo("alpha", FindingSql)], [], ReadableStyle.Markdown);

        var topHeading = rendered.IndexOf("# SilentScan corpus scan", StringComparison.Ordinal);
        var repoHeading = rendered.IndexOf("## alpha", StringComparison.Ordinal);
        var summaryHeading = rendered.IndexOf("### Summary", StringComparison.Ordinal);

        Assert.True(topHeading >= 0, "expected the top-level corpus heading");
        Assert.True(repoHeading >= 0, "expected the repo heading");
        Assert.True(summaryHeading >= 0, "expected the repo's own Summary heading");

        Assert.True(topHeading < repoHeading, "the corpus heading must outrank the repo heading");
        Assert.True(repoHeading < summaryHeading, "the repo's Summary must be nested under its own repo heading");
    }
}
