using SilentScan.Cli.Commands;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Readable;
using SilentScan.Core.Rules;
using SilentScan.Core.TypeInference;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Commands;

public sealed class ReportOutputTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"silentscan-report-output-test-{Guid.NewGuid():N}.txt");

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    [Theory]
    [InlineData("text", "Text")]
    [InlineData("markdown", "Markdown")]
    [InlineData("json", "Json")]
    [InlineData("sarif", "Sarif")]
    public void TryParseFormat_KnownFormat_ReturnsTrueAndParsedValue(string input, string expectedName)
    {
        var ok = ReportOutput.TryParseFormat(input, out var parsed);

        Assert.True(ok);
        Assert.Equal(expectedName, parsed.ToString());
    }

    [Fact]
    public void TryParseFormat_UnknownFormat_ReturnsFalseAndDefaultsToText()
    {
        var ok = ReportOutput.TryParseFormat("xml", out var parsed);

        Assert.False(ok);
        Assert.Equal(ReportFormat.Text, parsed);
    }

    [Fact]
    public void UnknownFormatMessage_IncludesOfferedValueAndAllExpectedOptions()
    {
        var message = ReportOutput.UnknownFormatMessage("xml");

        Assert.Contains("'xml'", message);
        Assert.Contains("'text'", message);
        Assert.Contains("'markdown'", message);
        Assert.Contains("'json'", message);
        Assert.Contains("'sarif'", message);
    }

    [Fact]
    public void ToStyle_OnlyMarkdownMapsToMarkdownStyle()
    {
        Assert.Equal(ReadableStyle.Markdown, ReportOutput.ToStyle(ReportFormat.Markdown));
        Assert.Equal(ReadableStyle.Text, ReportOutput.ToStyle(ReportFormat.Text));
        Assert.Equal(ReadableStyle.Text, ReportOutput.ToStyle(ReportFormat.Json));
        Assert.Equal(ReadableStyle.Text, ReportOutput.ToStyle(ReportFormat.Sarif));
    }

    [Theory]
    [InlineData("brief", ReadableVerbosity.Brief)]
    [InlineData("full", ReadableVerbosity.Full)]
    public void TryParseVerbosity_KnownVerbosity_ReturnsTrueAndParsedValue(string input, ReadableVerbosity expected)
    {
        var ok = ReportOutput.TryParseVerbosity(input, out var parsed);

        Assert.True(ok);
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void TryParseVerbosity_UnknownVerbosity_ReturnsFalseAndDefaultsToBrief()
    {
        var ok = ReportOutput.TryParseVerbosity("verbose", out var parsed);

        Assert.False(ok);
        Assert.Equal(ReadableVerbosity.Brief, parsed);
    }

    [Fact]
    public void UnknownVerbosityMessage_IncludesOfferedValueAndAllExpectedOptions()
    {
        var message = ReportOutput.UnknownVerbosityMessage("verbose");

        Assert.Contains("'verbose'", message);
        Assert.Contains("'brief'", message);
        Assert.Contains("'full'", message);
    }

    [Theory]
    [InlineData("high", true)]
    [InlineData("extreme", false)]
    public void TryParseConfidence_DelegatesToFindingConfidenceParsing(string input, bool expectedOk)
    {
        var ok = ReportOutput.TryParseConfidence(input, out var parsed);

        Assert.Equal(expectedOk, ok);
        if (expectedOk)
        {
            Assert.Equal(FindingConfidence.High, parsed);
        }
    }

    [Fact]
    public void UnknownConfidenceMessage_DelegatesToFindingConfidenceParsing()
    {
        Assert.Equal(FindingConfidenceParsing.UnknownConfidenceMessage("extreme"), ReportOutput.UnknownConfidenceMessage("extreme"));
    }

    [Fact]
    public void Emit_NullOutputPath_WritesToStdoutOnlyAndReturnsTrue()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var ok = ReportOutput.Emit("hello", null, stdout, stderr);

        Assert.True(ok);
        Assert.Equal("hello" + Environment.NewLine, stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void Emit_ValidOutputPath_WritesFileAndStderrNoticeAndReturnsTrue()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var ok = ReportOutput.Emit("report body", _tempFile, stdout, stderr);

        Assert.True(ok);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal("report body", File.ReadAllText(_tempFile));
        Assert.Contains(_tempFile, stderr.ToString());
    }

    [Fact]
    public void Emit_UnwritableOutputPath_WritesErrorToStderrAndReturnsFalse()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var badPath = Path.Combine(_tempFile + "-missing-dir", "report.txt");

        var ok = ReportOutput.Emit("report body", badPath, stdout, stderr);

        Assert.False(ok);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("could not write the report", stderr.ToString());
        Assert.False(File.Exists(badPath));
    }

    [Fact]
    public void HasCoverageGaps_CleanReport_ReturnsFalse()
    {
        var report = TestScanReports.Build();

        Assert.False(ReportOutput.HasCoverageGaps(report));
    }

    [Fact]
    public void HasCoverageGaps_SkippedConstructs_ReturnsTrue()
    {
        var skipped = new SkippedConstruct(AnalysisPass.Predicates, "test.sql", 1, 1, "MERGE", "unsupported-syntax");
        var report = TestScanReports.Build(
            SkippedConstructs: [skipped],
            SkippedConstructSummary: SkippedConstructSummary.From([skipped]));

        Assert.True(ReportOutput.HasCoverageGaps(report));
    }

    [Fact]
    public void HasCoverageGaps_UnanalyzableDynamicSql_ReturnsTrue()
    {
        var summary = DynamicSqlSummary.From([new DynamicSqlFinding("test.sql", 3, 5, DynamicSqlOutcome.Unanalyzable, "non-literal-argument")]);
        var report = TestScanReports.Build(DynamicSqlSummary: summary);

        Assert.True(ReportOutput.HasCoverageGaps(report));
    }

    [Fact]
    public void HasCoverageGaps_UnknownTypedPredicates_ReturnsTrue()
    {
        var summary = TypedPredicateSummary.From([new TypedPredicateFinding(
            Verdict.Unknown,
            new PredicateOperand.Column("dbo.T", "Col", new SqlType(SqlTypeCategory.VarChar), Indexed: true, Depth: 0, Provenance: null!),
            new PredicateOperand.Value(null),
            "=",
            "test.sql",
            1,
            1)]);
        var report = TestScanReports.Build(TypedPredicateSummary: summary);

        Assert.True(ReportOutput.HasCoverageGaps(report));
    }
}
