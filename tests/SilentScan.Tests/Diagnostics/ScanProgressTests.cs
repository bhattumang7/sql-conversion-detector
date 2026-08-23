using System.Globalization;
using SilentScan.Core.Diagnostics;

namespace SilentScan.Tests.Diagnostics;

public sealed class ScanProgressTests
{
    [Fact]
    public void Begin_WritesAndFlushesStageNameBeforeStageCompletes()
    {
        var writer = new StringWriter();
        var progress = new TextWriterScanProgress(writer);

        using var stage = progress.Begin("reading catalog");

        Assert.Equal("reading catalog... ", writer.ToString());
    }

    [Fact]
    public void CompletedStage_EmitsDetailAndElapsedOnTheSameLine()
    {
        var writer = new StringWriter();
        var progress = new TextWriterScanProgress(writer);

        using (var stage = progress.Begin("reading catalog"))
        {
            stage.Complete("1,284 tables, 9,113 columns");
        }

        var output = writer.ToString();
        Assert.Matches(@"^reading catalog\.\.\. 1,284 tables, 9,113 columns \(\d+\.\d+s\)\r?\n$", output);
    }

    [Fact]
    public void StageWithoutExplicitDetail_FallsBackToTheAdvanceCounter()
    {
        var writer = new StringWriter();
        var progress = new TextWriterScanProgress(writer);

        using (var stage = progress.Begin("parsing modules", total: 3))
        {
            stage.Advance();
            stage.Advance(2);
        }

        Assert.Matches(@"^parsing modules\.\.\. 3/3 \(\d+\.\d+s\)\r?\n$", writer.ToString());
    }

    [Fact]
    public void Advance_FromManyThreadsCountsEveryItemExactlyOnce()
    {
        const int total = 5_000;
        var writer = new StringWriter();
        var progress = new TextWriterScanProgress(writer);

        using (var stage = progress.Begin("scanning typed predicates", total))
        {
            Parallel.For(0, total, _ => stage.Advance());
        }

        Assert.Matches(@"^scanning typed predicates\.\.\. 5,000/5,000 \(\d+\.\d+s\)\r?\n$", writer.ToString());
    }

    [Fact]
    public void FastStage_EmitsNoHeartbeatsSoTheLineStaysASingleCleanStageLine()
    {
        var writer = new StringWriter();
        var progress = new TextWriterScanProgress(writer);

        using (var stage = progress.Begin("parsing modules", total: 1_000))
        {
            for (var i = 0; i < 1_000; i++)
            {
                stage.Advance();
            }

            stage.Complete("1,000 modules");
        }

        Assert.Matches(@"^parsing modules\.\.\. 1,000 modules \(\d+\.\d+s\)\r?\n$", writer.ToString());
    }

    [Fact]
    public void Dispose_IsIdempotentAndEmitsExactlyOneLine()
    {
        var writer = new StringWriter();
        var progress = new TextWriterScanProgress(writer);

        var stage = progress.Begin("resolving lineage");
        stage.Complete("12 relations");
        stage.Dispose();
        stage.Dispose();

        var line = Assert.Single(writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("resolving lineage", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Done_EmitsTheOverallElapsedLine()
    {
        var writer = new StringWriter();
        var progress = new TextWriterScanProgress(writer);

        progress.Done(TimeSpan.FromSeconds(105.14));

        Assert.Equal($"done in 105.1s{Environment.NewLine}", writer.ToString());
    }

    [Fact]
    public void Done_FormatsInvariantlyRegardlessOfAmbientCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var writer = new StringWriter();

            new TextWriterScanProgress(writer).Done(TimeSpan.FromSeconds(2.5));

            Assert.Contains("2.5s", writer.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("2,5s", writer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void NullScanProgress_WritesNothingAndStillHandsBackAUsableStage()
    {
        var progress = NullScanProgress.Instance;

        using var stage = progress.Begin("reading catalog", total: 10);
        stage.Advance(5);
        stage.Complete("ignored");
        progress.Done(TimeSpan.FromSeconds(1));

        Assert.NotNull(stage);
    }

    [Fact]
    public void StageThatThrows_EmitsElapsedOnlyRatherThanAMisleadingZeroCount()
    {
        var writer = new StringWriter();
        var progress = new TextWriterScanProgress(writer);

        var body = void (IScanProgress p) =>
        {
            using var stage = p.Begin("reading catalog");
            throw new InvalidOperationException("connection refused");
        };

        Assert.Throws<InvalidOperationException>(() => body(progress));

        Assert.Matches(@"^reading catalog\.\.\. \(\d+\.\d+s\)\r?\n$", writer.ToString());
        Assert.DoesNotContain(" 0 ", writer.ToString(), StringComparison.Ordinal);
    }
}
