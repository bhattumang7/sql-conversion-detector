using SilentScan.Cli.Commands;

namespace SilentScan.Tests.Commands;

/// <summary>
/// The command surface around the readable report: that it is what a user gets without asking
/// for anything, that --format json still produces exactly the machine output it always did,
/// and that --output either writes the file or says why it could not - a report the user asked
/// to keep and that silently went nowhere is worse than no report.
/// </summary>
public sealed class ReadableOutputTests : IDisposable
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "fixtures", "tier1", "FUNCTION_WRAPPED_COLUMN_fires.sql");

    private readonly string _tempDir = Directory.CreateTempSubdirectory("silentscan-readable-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void Scan_DefaultFormat_IsTheReadableReport()
    {
        var parsed = ScanCommand.Create().Parse([FixturePath]);

        Assert.Equal("text", parsed.GetValue<string>("--format"));
    }

    [Fact]
    public void Scan_TextFormat_ReadsAsAReportRatherThanJson()
    {
        var stdout = new StringWriter();

        var exitCode = ScanCommand.Run(FixturePath, "text", ".sql", null, stdout, new StringWriter());

        var output = stdout.ToString();
        Assert.Equal(0, exitCode);
        Assert.StartsWith("SilentScan - ", output, StringComparison.Ordinal);
        Assert.Contains("Column wrapped in a function", output, StringComparison.Ordinal);
        Assert.Contains("SomeDate", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Kind\":", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_MarkdownFormat_EmitsMarkdownHeadingsAndTables()
    {
        var stdout = new StringWriter();

        ScanCommand.Run(FixturePath, "markdown", ".sql", null, stdout, new StringWriter());

        var output = stdout.ToString();
        Assert.Contains("## Summary", output, StringComparison.Ordinal);
        Assert.Contains("| Where | Column | Indexed | Detail |", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_Output_WritesTheReportToTheFileAndNotToStdout()
    {
        var reportPath = Path.Combine(_tempDir, "report.md");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = ScanCommand.Run(FixturePath, "markdown", ".sql", null, stdout, stderr, reportPath);

        Assert.Equal(0, exitCode);
        Assert.Empty(stdout.ToString());
        Assert.Contains($"wrote report to {reportPath}", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("## Summary", File.ReadAllText(reportPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_OutputToAnUnwritablePath_FailsLoudly()
    {
        var reportPath = Path.Combine(_tempDir, "no-such-directory", "report.md");
        var stderr = new StringWriter();

        var exitCode = ScanCommand.Run(FixturePath, "text", ".sql", null, new StringWriter(), stderr, reportPath);

        Assert.Equal(1, exitCode);
        Assert.Contains("could not write the report", stderr.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(reportPath));
    }

    [Fact]
    public void Scan_UnknownFormat_NamesEveryFormatItAccepts()
    {
        var stderr = new StringWriter();

        var exitCode = ScanCommand.Run(FixturePath, "html", ".sql", null, new StringWriter(), stderr);

        Assert.Equal(1, exitCode);
        var message = stderr.ToString();
        Assert.Contains("'text', 'markdown', 'json' or 'sarif'", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScanCorpus_Sarif_IsRefusedRatherThanMergingReposIntoOneLog()
    {
        var manifestPath = Path.Combine(_tempDir, "manifest.json");
        File.WriteAllText(manifestPath, """{"repos": []}""");
        var stderr = new StringWriter();

        var exitCode = ScanCorpusCommand.Run(manifestPath, _tempDir, new StringWriter(), stderr, "sarif");

        Assert.Equal(1, exitCode);
        Assert.Contains("does not support --format sarif", stderr.ToString(), StringComparison.Ordinal);
    }
}
