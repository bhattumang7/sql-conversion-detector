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

        var command = new Command("scan", "Parse .sql files and report parse health plus sargability findings.")
        {
            pathArgument,
            formatOption,
            extensionsOption,
        };

        command.SetAction(parseResult =>
        {
            var path = parseResult.GetValue(pathArgument)!;
            var format = parseResult.GetValue(formatOption)!;
            var extensions = parseResult.GetValue(extensionsOption)!;
            return Run(path, format, extensions, Console.Out, Console.Error);
        });

        return command;
    }

    internal static int Run(string path, string format, string extensions, TextWriter stdout, TextWriter stderr)
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
        var report = ScanReportBuilder.Build(files);

        stdout.WriteLine(format == "sarif" ? SarifReportWriter.Write(report) : JsonSerializer.Serialize(report, JsonOptions));

        return report.ParseHealth.FilesWithErrors == 0 ? 0 : 1;
    }
}
