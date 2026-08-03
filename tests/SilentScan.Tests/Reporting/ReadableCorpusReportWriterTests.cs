using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Readable;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Reporting;

/// <summary>
/// The corpus report's rollup table is what answers "which repo do I look at" without reading
/// five reports first, so it has to carry the facts that decide that: how many findings, and
/// whether the repo's files actually parsed as T-SQL. A repo below the parse-success bar must be
/// visibly marked rather than sitting in the table looking like any other row.
/// </summary>
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

        Assert.Contains("pass", alpha, StringComparison.Ordinal);

        // Each repo's own full report follows the table.
        Assert.Contains("dbo.Users.DisplayName", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RepoBelowTheDialectSniffingBar_IsMarkedAndNamed()
    {
        // A malformed batch is real SQL Server syntax, not just noise ScriptDOM can recover
        // from - deploying it through the engine would abort deployment outright (ScriptDeployer
        // has no per-batch try/catch), so this exercises ParseHealthReport directly, the same way
        // ScanReportBuilder itself does before any catalog/lineage work runs.
        var broken = SqlScriptParser.ParseText("broken.sql", "SELECT FROM WHERE ORDER;");
        var report = ScanReportBuilder.BuildFromParseResults([broken], new DatabaseCatalog());
        var repo = new ReadableCorpusRepo("mysql-ish", report, null);

        var rendered = ReadableCorpusReportWriter.Write([repo], [], ReadableStyle.Text);

        Assert.Contains("BELOW BAR", rendered, StringComparison.Ordinal);
        Assert.Contains("parse-success bar the corpus uses", rendered, StringComparison.Ordinal);
        Assert.Contains("mysql-ish", rendered, StringComparison.Ordinal);
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

        Assert.Contains("# SilentScan corpus scan", rendered, StringComparison.Ordinal);
        Assert.Contains("## alpha", rendered, StringComparison.Ordinal);
        Assert.Contains("### Summary", rendered, StringComparison.Ordinal);
    }
}
