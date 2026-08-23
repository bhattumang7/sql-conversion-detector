using System.Globalization;

namespace SilentScan.Core.Reporting.Readable;

public sealed record ReadableCorpusRepo(string Name, ScanReport Report, string? PathBase = null);

public static class ReadableCorpusReportWriter
{
    public static string Write(
        IReadOnlyList<ReadableCorpusRepo> repos,
        IReadOnlyList<string> reposWithoutClones,
        ReadableStyle style,
        ReadableVerbosity verbosity = ReadableVerbosity.Brief)
    {
        ArgumentNullException.ThrowIfNull(repos);
        ArgumentNullException.ThrowIfNull(reposWithoutClones);

        List<ReadableBlock> blocks =
        [
            new ReadableBlock.Heading(1, "SilentScan corpus scan"),
            new ReadableBlock.Heading(2, "All repos"),
        ];

        if (repos.Count == 0)
        {
            blocks.Add(new ReadableBlock.Paragraph("No repo in the manifest had a local clone to scan."));
        }
        else
        {
            blocks.Add(new ReadableBlock.Table(
                ["Repo", "Files", "Parsed", "Dialect check", "Scan forced", "Degraded seek", "Unclassified"],
                [.. repos.Select(RollupRow)]));
        }

        var belowBar = repos.Where(r => !r.Report.ParseHealth.PassesDialectSniffing).Select(r => r.Name).ToList();
        if (belowBar.Count > 0)
        {
            blocks.Add(new ReadableBlock.Paragraph(
                $"Below the {ReadableScanReportWriter.Percent(ParseHealthReport.MinimumAcceptableParseSuccessRate)} parse-success bar the corpus uses to tell T-SQL from another dialect: " +
                $"{string.Join(", ", belowBar)}. Their findings are reported in full below, but a repo whose files mostly do not parse as T-SQL produces noise, not evidence."));
        }

        if (reposWithoutClones.Count > 0)
        {
            blocks.Add(new ReadableBlock.Paragraph("Declared in the manifest but not scanned - no local clone was found:"));
            blocks.Add(new ReadableBlock.Bullets(reposWithoutClones));
        }

        foreach (var repo in repos)
        {
            blocks.Add(new ReadableBlock.Heading(2, repo.Name));
            blocks.AddRange(ReadableScanReportWriter.BuildSections(repo.Report, 3, repo.PathBase, verbosity));
        }

        return ReadableDocumentRenderer.Render(new ReadableDocument(blocks), style);
    }

    private static IReadOnlyList<string> RollupRow(ReadableCorpusRepo repo)
    {
        var health = repo.Report.ParseHealth;
        var summary = repo.Report.TypedPredicateSummary;

        return
        [
            repo.Name,
            health.TotalFiles.ToString(CultureInfo.InvariantCulture),
            ReadableScanReportWriter.Percent(health.ParseSuccessRate),
            health.PassesDialectSniffing ? "pass" : "BELOW BAR",
            Occurrences(summary.ScanForcedCount, summary.DistinctScanForcedCount),
            Occurrences(summary.RangeSeekCount, summary.DistinctRangeSeekCount),
            summary.UnknownCount.ToString(CultureInfo.InvariantCulture),
        ];
    }

    private static string Occurrences(int occurrences, int distinct) =>
        occurrences == distinct
            ? occurrences.ToString(CultureInfo.InvariantCulture)
            : $"{occurrences.ToString(CultureInfo.InvariantCulture)} ({distinct.ToString(CultureInfo.InvariantCulture)} distinct)";
}
