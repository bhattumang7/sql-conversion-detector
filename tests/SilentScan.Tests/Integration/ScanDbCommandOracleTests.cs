using System.Text.Json;
using SilentScan.Cli.Commands;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class ScanDbCommandOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(ScanDbCommandOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Widgets (
            WidgetId INT NOT NULL PRIMARY KEY,
            WidgetCode varchar(30) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            INDEX IX_WidgetCode (WidgetCode));
        GO
        CREATE PROCEDURE dbo.usp_FindWidget @WidgetCode NVARCHAR(30)
        AS
        BEGIN
            SELECT WidgetId FROM dbo.Widgets WHERE WidgetCode = @WidgetCode;
        END
        """;

    [Fact]
    public async Task RunAsync_CleanDatabaseTextFormat_ReturnsZeroAndWritesReadableReportWithProgressOnStderr()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var options = new ReportOptions("text", "high", null, "brief");

        var exitCode = await ScanDbCommand.RunAsync(
            Options.BuildConnectionString(DatabaseName), includePlanCacheEvidence: false, fetchSqlFromTables: false, options, stdout, stderr, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("usp_FindWidget", stdout.ToString());
        Assert.Contains("reading catalog", stderr.ToString());
        Assert.Contains("done in", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_JsonFormat_ReturnsZeroAndWritesParseableJsonWithModuleCount()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var options = new ReportOptions("json", "high", null, "brief");

        var exitCode = await ScanDbCommand.RunAsync(
            Options.BuildConnectionString(DatabaseName), includePlanCacheEvidence: false, fetchSqlFromTables: false, options, stdout, stderr, CancellationToken.None);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(1, document.RootElement.GetProperty("ModulesAnalyzed").GetInt32());
    }

    [Fact]
    public async Task RunAsync_SarifFormat_ReturnsZeroAndWritesSarifSchemaDocument()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var options = new ReportOptions("sarif", "high", null, "brief");

        var exitCode = await ScanDbCommand.RunAsync(
            Options.BuildConnectionString(DatabaseName), includePlanCacheEvidence: false, fetchSqlFromTables: false, options, stdout, stderr, CancellationToken.None);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(stdout.ToString());
        Assert.True(document.RootElement.TryGetProperty("runs", out _));
        Assert.True(document.RootElement.TryGetProperty("version", out _));
    }

    [Fact]
    public async Task RunAsync_OutputPathGiven_WritesReportToFileAndConfirmsOnStderrInsteadOfStdout()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"silentscan-scan-db-output-{Guid.NewGuid():N}.txt");
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var options = new ReportOptions("text", "high", outputPath, "brief");

            var exitCode = await ScanDbCommand.RunAsync(
                Options.BuildConnectionString(DatabaseName), includePlanCacheEvidence: false, fetchSqlFromTables: false, options, stdout, stderr, CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("usp_FindWidget", stdout.ToString());
            Assert.Contains($"wrote report to {outputPath}", stderr.ToString());
            Assert.True(File.Exists(outputPath));
            Assert.Contains("usp_FindWidget", File.ReadAllText(outputPath));
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public async Task RunAsync_OutputPathUnwritable_ReturnsOneAndWritesEmitErrorWithoutSuppressingScanResults()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"silentscan-scan-db-missing-{Guid.NewGuid():N}", "out.txt");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var options = new ReportOptions("text", "high", outputPath, "brief");

        var exitCode = await ScanDbCommand.RunAsync(
            Options.BuildConnectionString(DatabaseName), includePlanCacheEvidence: false, fetchSqlFromTables: false, options, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains($"error: could not write the report to {outputPath}", stderr.ToString());
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task RunAsync_PlanCacheEvidenceRequested_StillCompletesAndReturnsZero()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var options = new ReportOptions("text", "high", null, "brief");

        var exitCode = await ScanDbCommand.RunAsync(
            Options.BuildConnectionString(DatabaseName), includePlanCacheEvidence: true, fetchSqlFromTables: false, options, stdout, stderr, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("usp_FindWidget", stdout.ToString());
    }
}
