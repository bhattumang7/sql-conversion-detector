using SilentScan.Cli.Commands;

namespace SilentScan.Tests.Commands;

public sealed class ScanDbCommandTests
{
    [Fact]
    public async Task RunAsync_UnknownFormat_ReturnsOneAndWritesErrorWithoutConnecting()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var options = new ReportOptions("xml", "high", null, "brief");

        var exitCode = await ScanDbCommand.RunAsync(
            "Server=nonexistent-host;Database=db", includePlanCacheEvidence: false, fetchSqlFromTables: false, strict: false, options, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("unknown --format", stderr.ToString());
        Assert.Equal(string.Empty, stdout.ToString());
    }

    [Fact]
    public async Task RunAsync_UnknownConfidence_ReturnsOneAndWritesErrorWithoutConnecting()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var options = new ReportOptions("text", "extreme", null, "brief");

        var exitCode = await ScanDbCommand.RunAsync(
            "Server=nonexistent-host;Database=db", includePlanCacheEvidence: false, fetchSqlFromTables: false, strict: false, options, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("unknown --confidence", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_UnknownVerbosity_ReturnsOneAndWritesErrorWithoutConnecting()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var options = new ReportOptions("text", "high", null, "verbose");

        var exitCode = await ScanDbCommand.RunAsync(
            "Server=nonexistent-host;Database=db", includePlanCacheEvidence: false, fetchSqlFromTables: false, strict: false, options, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("unknown --verbosity", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_ValidOptionsButUnreachableServer_ReturnsOneAndWritesConnectionError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var options = new ReportOptions("text", "high", null, "brief");

        var exitCode = await ScanDbCommand.RunAsync(
            "Server=127.0.0.1,1;Database=db;Connect Timeout=1;TrustServerCertificate=true",
            includePlanCacheEvidence: false, fetchSqlFromTables: false, strict: false, options, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("error: could not scan the live database", stderr.ToString());
    }
}
