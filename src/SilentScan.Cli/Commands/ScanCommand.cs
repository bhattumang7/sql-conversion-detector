using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Cli.Commands;

/// <summary>
/// `silentscan scan &lt;path&gt;` — parses every .sql file under the given folder (or a single
/// file), reports ScriptDOM parse health (Pass 0 / the corpus dialect-sniffing signal), and
/// for files that parsed cleanly, the Tier-1 syntactic and typed-verdict sargability findings
/// (CLAUDE.md Pass 1-4). Supports JSON (default) or SARIF output for CI gating.
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
            Description = "Output format: json (default) or sarif.",
            DefaultValueFactory = _ => "json",
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
        };

        command.SetAction(parseResult =>
        {
            var path = parseResult.GetValue(pathArgument)!;
            var format = parseResult.GetValue(formatOption)!;
            var extensions = parseResult.GetValue(extensionsOption)!;
            var collation = parseResult.GetValue(collationOption);
            return Run(path, format, extensions, collation, Console.Out, Console.Error);
        });

        return command;
    }

    internal static int Run(string path, string format, string extensions, string? collation, TextWriter stdout, TextWriter stderr)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            stderr.WriteLine($"error: path not found: {path}");
            return 1;
        }

        if (format is not ("json" or "sarif"))
        {
            stderr.WriteLine($"error: unknown --format '{format}' (expected 'json' or 'sarif')");
            return 1;
        }

        var extensionList = extensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var files = SqlFileDiscovery.EnumerateSqlFiles(path, extensionList);
        var parseResults = files.Select(SqlScriptParser.ParseFile).ToList();
        var report = ScanReportBuilder.BuildFromParseResults(parseResults, collation);

        if (format == "sarif")
        {
            stdout.WriteLine(SarifReportWriter.Write(report));
        }
        else
        {
            // No pinned collation means the flagship rule is structurally unreachable for any
            // column relying on the database default (CollationSensitivityReport's own doc: an
            // unqualified zero here looks identical to "we checked and there's nothing," a
            // materially different and stronger claim than what was actually established) - the
            // same honesty CLAUDE.md already requires scan-corpus to give an unpinned repo.
            var collationSensitivity = collation is null ? CollationSensitivityReport.Analyze(parseResults) : null;
            stdout.WriteLine(JsonSerializer.Serialize(new ScanCommandResult(report, collationSensitivity), JsonOptions));
        }

        return report.ParseHealth.FilesWithErrors == 0 ? 0 : 1;
    }
}

/// <summary>One `scan` run's ordinary report, plus a collation-sensitivity re-run when no --collation was pinned (null when one was - there's nothing to be sensitive to). Mirrors scan-corpus's identical CorpusRepoScanResult shape.</summary>
internal sealed record ScanCommandResult(ScanReport Report, CollationSensitivityReport? CollationSensitivity);
