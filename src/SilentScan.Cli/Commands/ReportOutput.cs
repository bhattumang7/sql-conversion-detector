using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Readable;

namespace SilentScan.Cli.Commands;

/// <summary>
/// The output formats every scan command accepts, and the one place a rendered report is
/// actually delivered. <see cref="Text"/> is the default: the JSON is complete but is a poor
/// thing to read, and a reader looking for what to fix should not have to walk it. The machine
/// formats are unchanged and still one flag away.
/// </summary>
internal enum ReportFormat
{
    Text,
    Markdown,
    Json,
    Sarif,
}

/// <summary>The report-shaping flags every scan command's own <c>RunAsync</c> takes together - bundled into one value so a caller doesn't add a bare parameter for every new flag (format, then confidence, then whatever comes next) and blow through Sonar's per-method parameter budget.</summary>
internal readonly record struct ReportOptions(string Format, string Confidence, string? OutputPath);

internal static class ReportOutput
{
    internal const string FormatOptionDescription =
        "Output format: text (default, human-readable), markdown (the same report as a shareable document), json (the complete findings schema), or sarif (for CI).";

    internal const string OutputOptionDescription =
        "Write the report to this file instead of standard output. The parent directory must exist.";

    internal const string ConfidenceOptionDescription =
        "The least confident a finding may be and still be reported: high (default - only findings resting on real, provably-constant source text) or medium (also includes a dynamic-SQL finding derived from a value this scan could not prove constant, e.g. a symbolic placeholder standing in for an uninitialized or caller-unknown variable). Low is not yet produced by anything in this tool.";

    internal static bool TryParseConfidence(string confidence, out FindingConfidence parsed)
    {
        switch (confidence)
        {
            case "high":
                parsed = FindingConfidence.High;
                return true;
            case "medium":
                parsed = FindingConfidence.Medium;
                return true;
            case "low":
                parsed = FindingConfidence.Low;
                return true;
            default:
                parsed = FindingConfidence.High;
                return false;
        }
    }

    internal static string UnknownConfidenceMessage(string confidence) =>
        $"error: unknown --confidence '{confidence}' (expected 'high', 'medium' or 'low')";

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

    /// <summary>
    /// Sends a rendered report to its destination. Returns false, having explained itself on
    /// <paramref name="stderr"/>, when a --output path could not be written - a report the user
    /// asked to keep and that silently went nowhere is worse than no report at all.
    /// </summary>
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

        // Confirmation goes to stderr, not stdout: stdout is what a caller redirects or pipes,
        // and this line is not part of the report.
        stderr.WriteLine($"wrote report to {outputPath}");
        return true;
    }
}
