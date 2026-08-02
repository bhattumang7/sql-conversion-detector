using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Readable;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Cli.Commands;

/// <summary>
/// `silentscan scan &lt;path&gt;` — parses every .sql file under the given folder (or a single
/// file), reports ScriptDOM parse health (Pass 0 / the corpus dialect-sniffing signal), and
/// for files that parsed cleanly, the Tier-1 syntactic and typed-verdict sargability findings
/// (CLAUDE.md Pass 1-4). Renders as readable text (default) or markdown, or as the full JSON
/// findings schema or SARIF for CI gating.
/// </summary>
public static class ScanCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Findings schema is versioned JSON (CLAUDE.md) - enum names, not raw ordinals,
        // so the schema stays stable as new finding kinds are added.
        Converters = { new JsonStringEnumConverter() },
    };

    public static Command Create()
    {
        var pathArgument = new Argument<string>("path")
        {
            Description = "A .sql file or a folder to scan recursively.",
        };

        var formatOption = new Option<string>("--format")
        {
            Description = ReportOutput.FormatOptionDescription,
            DefaultValueFactory = _ => "text",
        };

        var outputOption = new Option<string?>("--output")
        {
            Description = ReportOutput.OutputOptionDescription,
        };

        var extensionsOption = new Option<string>("--extensions")
        {
            Description = "Comma-separated file extensions to scan (default: .sql). Some repos ship DDL under a different extension, e.g. DNN Platform's .SqlDataProvider.",
            DefaultValueFactory = _ => ".sql",
        };

        var collationOption = new Option<string?>("--collation")
        {
            Description = "The database's default collation, used for any string-family column with no per-column COLLATE clause of its own. Without this, the flagship varchar-vs-nvarchar rule is structurally unreachable for such columns (VerdictClassifier never guesses an unresolved collation) - the single most common real-world shape, since most DDL doesn't repeat a per-column COLLATE that already matches the database default. Omit to instead get a collationSensitivity report scored under both a representative SQL_* and a representative Windows collation, so you see what the finding count would be either way rather than a silent zero.",
        };

        var command = new Command("scan", "Parse .sql files and report parse health plus sargability findings.")
        {
            pathArgument,
            formatOption,
            extensionsOption,
            collationOption,
            outputOption,
        };

        command.SetAction(parseResult =>
        {
            var path = parseResult.GetValue(pathArgument)!;
            var format = parseResult.GetValue(formatOption)!;
            var extensions = parseResult.GetValue(extensionsOption)!;
            var collation = parseResult.GetValue(collationOption);
            var output = parseResult.GetValue(outputOption);
            return Run(path, format, extensions, collation, Console.Out, Console.Error, output);
        });

        return command;
    }

    internal static int Run(string path, string format, string extensions, string? collation, TextWriter stdout, TextWriter stderr, string? outputPath = null)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            stderr.WriteLine($"error: path not found: {path}");
            return 1;
        }

        if (!ReportOutput.TryParseFormat(format, out var reportFormat))
        {
            stderr.WriteLine(ReportOutput.UnknownFormatMessage(format));
            return 1;
        }

        var extensionList = extensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var files = SqlFileDiscovery.EnumerateSqlFiles(path, extensionList);
        var parseResults = files.Select(SqlScriptParser.ParseFile).ToList();
        var report = ScanReportBuilder.BuildFromParseResults(parseResults, collation);

        // No pinned collation means the flagship rule is structurally unreachable for any
        // column relying on the database default (CollationSensitivityReport's own doc: an
        // unqualified zero here looks identical to "we checked and there's nothing," a
        // materially different and stronger claim than what was actually established) - the
        // same honesty CLAUDE.md already requires scan-corpus to give an unpinned repo.
        // Computed for every format except SARIF, which has no place to carry it.
        var collationSensitivity = collation is null && reportFormat != ReportFormat.Sarif
            ? CollationSensitivityReport.Analyze(parseResults)
            : null;

        var content = reportFormat switch
        {
            ReportFormat.Sarif => SarifReportWriter.Write(report),
            ReportFormat.Json => JsonSerializer.Serialize(new ScanCommandResult(report, collationSensitivity), JsonOptions),
            _ => ReadableScanReportWriter.Write(
                report,
                collationSensitivity,
                $"SilentScan - {path}",
                ReportOutput.ToStyle(reportFormat),
                PathBaseFor(path)),
        };

        if (!ReportOutput.Emit(content, outputPath, stdout, stderr))
        {
            return 1;
        }

        return report.ParseHealth.FilesWithErrors == 0 ? 0 : 1;
    }

    /// <summary>
    /// The directory that finding paths in the readable report are shown relative to. A single
    /// file's own path is not a base to trim - doing so would leave every finding in it with no
    /// file name at all.
    /// </summary>
    private static string? PathBaseFor(string path) => Directory.Exists(path) ? path : null;
}

/// <summary>One `scan` run's ordinary report, plus a collation-sensitivity re-run when no --collation was pinned (null when one was - there's nothing to be sensitive to). Mirrors scan-corpus's identical CorpusRepoScanResult shape.</summary>
internal sealed record ScanCommandResult(ScanReport Report, CollationSensitivityReport? CollationSensitivity);
