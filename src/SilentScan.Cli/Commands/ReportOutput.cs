using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Readable;

namespace SilentScan.Cli.Commands;

internal enum ReportFormat
{
    Text,
    Markdown,
    Json,
    Sarif,
}

internal readonly record struct ReportOptions(string Format, string Confidence, string? OutputPath, string Verbosity);

internal static class ReportOutput
{
    internal const string FormatOptionDescription =
        "Output format: text (default, human-readable), markdown (the same report as a shareable document), json (the complete findings schema), or sarif (for CI).";

    internal const string OutputOptionDescription =
        "Write the report to this file instead of standard output. The parent directory must exist.";

    internal const string ConfidenceOptionDescription = FindingConfidenceParsing.OptionDescription;

    internal const string VerbosityOptionDescription =
        "How much detail the text/markdown report gives for sections about what could NOT be established (parse errors, unresolvable dynamic SQL, ambiguous types, stale metadata): brief (default - each such section states its count only) or full (every row, as the JSON carries it). Never affects json/sarif output, and never hides an actual finding - only these coverage/caveat sections.";

    internal const string StrictOptionDescription =
        "Fail the command (non-zero exit code) when the scan could not fully look at the code: parse errors, dropped/unanalyzed batches, skipped constructs, unanalyzable dynamic SQL, or predicates that could not be classified. Off by default - the same coverage gaps are always reported (stderr warnings, SARIF notifications), this flag only changes whether they also affect the exit code.";

    internal const string PlanCacheEvidenceOptionDescription =
        "Also read the live plan cache and rank findings by whether they are actually observed converting in a real cached plan, with execution counts. Requires VIEW SERVER STATE; off by default.";

    internal const string FetchSqlFromTablesOptionDescription =
        "Also fetch the real value(s) of dynamic SQL text stored in a table (e.g. SELECT @sql = Definition FROM dbo.Templates WHERE Name = 'X') instead of leaving it unanalyzable - narrowed by whatever literal WHERE conditions can be pushed down, every distinct value analyzed as its own candidate when more than one matches. Reads real row content, not just catalog metadata - off by default.";

    internal static bool HasCoverageGaps(ScanReport report) =>
        report.ParseHealth.Files.Any(f => f.Errors.Count > 0 || f.UnanalyzedBatches.Count > 0)
        || report.SkippedConstructSummary.TotalCount > 0
        || report.DynamicSqlSummary.UnanalyzableCount > 0
        || report.DynamicSqlSummary.InnerParseFailedCount > 0
        || report.DynamicSqlSummary.PartiallyAnalyzedCount > 0
        || report.TypedPredicateSummary.UnknownCount > 0;

    internal static bool TryParseConfidence(string confidence, out FindingConfidence parsed) =>
        FindingConfidenceParsing.TryParse(confidence, out parsed);

    internal static string UnknownConfidenceMessage(string confidence) =>
        FindingConfidenceParsing.UnknownConfidenceMessage(confidence);

    internal static bool TryParseFormat(string format, out ReportFormat parsed)
    {
        switch (format)
        {
            case "text":
                parsed = ReportFormat.Text;
                return true;
            case "markdown":
                parsed = ReportFormat.Markdown;
                return true;
            case "json":
                parsed = ReportFormat.Json;
                return true;
            case "sarif":
                parsed = ReportFormat.Sarif;
                return true;
            default:
                parsed = ReportFormat.Text;
                return false;
        }
    }

    internal static string UnknownFormatMessage(string format) =>
        $"error: unknown --format '{format}' (expected 'text', 'markdown', 'json' or 'sarif')";

    internal static ReadableStyle ToStyle(ReportFormat format) =>
        format == ReportFormat.Markdown ? ReadableStyle.Markdown : ReadableStyle.Text;

    internal static bool TryParseVerbosity(string verbosity, out ReadableVerbosity parsed)
    {
        switch (verbosity)
        {
            case "brief":
                parsed = ReadableVerbosity.Brief;
                return true;
            case "full":
                parsed = ReadableVerbosity.Full;
                return true;
            default:
                parsed = ReadableVerbosity.Brief;
                return false;
        }
    }

    internal static string UnknownVerbosityMessage(string verbosity) =>
        $"error: unknown --verbosity '{verbosity}' (expected 'brief' or 'full')";

    internal static bool Emit(string content, string? outputPath, TextWriter stdout, TextWriter stderr)
    {
        if (outputPath is null)
        {
            stdout.WriteLine(content);
            return true;
        }

        try
        {
            File.WriteAllText(outputPath, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            stderr.WriteLine($"error: could not write the report to {outputPath}: {ex.Message}");
            return false;
        }

        stderr.WriteLine($"wrote report to {outputPath}");
        return true;
    }
}
