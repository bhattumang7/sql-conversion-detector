using SilentScan.Cli.Commands;

namespace SilentScan.Tests.Commands;

public sealed class ScanCorpusLiveCommandTests : IDisposable
{
    private readonly string _manifestPath = Path.Combine(Path.GetTempPath(), $"silentscan-scan-corpus-live-test-{Guid.NewGuid():N}.json");

    public ScanCorpusLiveCommandTests() =>
        File.WriteAllText(_manifestPath, """
            {
              "repos": [
                {
                  "name": "example",
                  "url": "https://github.com/example/example",
                  "commitSha": "abcdef0123456789abcdef0123456789abcdef01",
                  "license": "MIT",
                  "ddlPaths": ["db/schema/**/*.sql"]
                }
              ]
            }
            """);

    public void Dispose() => File.Delete(_manifestPath);

    [Fact]
    public async Task RunAsync_MissingManifest_ReturnsOneAndWritesError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var options = new ReportOptions("text", "high", null, "brief");

        var exitCode = await ScanCorpusLiveCommand.RunAsync(
            "/no/such/manifest.json", "corpus/_clones", false, stdout, stderr, options, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("manifest not found", stderr.ToString());
        Assert.Equal(string.Empty, stdout.ToString());
    }

    [Fact]
    public async Task RunAsync_UnknownFormat_ReturnsOneAndWritesError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var options = new ReportOptions("xml", "high", null, "brief");

        var exitCode = await ScanCorpusLiveCommand.RunAsync(
            _manifestPath, "corpus/_clones", false, stdout, stderr, options, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("unknown --format", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_SarifFormat_IsRejectedWithGuidanceToUseScanDb()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var options = new ReportOptions("sarif", "high", null, "brief");

        var exitCode = await ScanCorpusLiveCommand.RunAsync(
            _manifestPath, "corpus/_clones", false, stdout, stderr, options, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("does not support --format sarif", stderr.ToString());
        Assert.Contains("scan-db", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_UnknownConfidence_ReturnsOneAndWritesError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var options = new ReportOptions("text", "extreme", null, "brief");

        var exitCode = await ScanCorpusLiveCommand.RunAsync(
            _manifestPath, "corpus/_clones", false, stdout, stderr, options, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("unknown --confidence", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_UnknownVerbosity_ReturnsOneAndWritesError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var options = new ReportOptions("text", "high", null, "verbose");

        var exitCode = await ScanCorpusLiveCommand.RunAsync(
            _manifestPath, "corpus/_clones", false, stdout, stderr, options, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("unknown --verbosity", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_RepoHasNoLocalClone_WritesWarningAndReturnsOneWithTextReport()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var options = new ReportOptions("text", "high", null, "brief");

        var exitCode = await ScanCorpusLiveCommand.RunAsync(
            _manifestPath, "/no/such/clones-root", false, stdout, stderr, options, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("'example' has no local clone at", stderr.ToString());
        Assert.Contains("example", stdout.ToString());
    }

    [Fact]
    public async Task RunAsync_RepoHasNoLocalClone_WritesJsonReportWhenFormatIsJson()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var options = new ReportOptions("json", "high", null, "brief");

        var exitCode = await ScanCorpusLiveCommand.RunAsync(
            _manifestPath, "/no/such/clones-root", false, stdout, stderr, options, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("'example' has no local clone at", stderr.ToString());
        Assert.Equal("{}", stdout.ToString().Trim());
    }

    [Fact]
    public async Task RunAsync_OutputPathGiven_WritesReportToFileInsteadOfStdout()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"silentscan-scan-corpus-live-output-{Guid.NewGuid():N}.txt");
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var options = new ReportOptions("text", "high", outputPath, "brief");

            var exitCode = await ScanCorpusLiveCommand.RunAsync(
                _manifestPath, "/no/such/clones-root", false, stdout, stderr, options, CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.True(File.Exists(outputPath));
            Assert.Contains("example", File.ReadAllText(outputPath));
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }
}
