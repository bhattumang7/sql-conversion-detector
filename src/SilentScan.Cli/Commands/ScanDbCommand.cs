using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Reporting.Sarif;
using SilentScan.Live;

namespace SilentScan.Cli.Commands;

public readonly record struct ScanFlags(bool IncludePlanCacheEvidence, bool FetchSqlFromTables, bool Strict);

public static class ScanDbCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(), new SilentScan.Core.Reporting.FindingJsonConverter() },
    };

    public static Command Create()
    {
        var connectionStringArgument = new Argument<string>("connection-string")
        {
            Description = "A SQL Server connection string (Microsoft.Data.SqlClient format).",
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

        var planCacheEvidenceOption = new Option<bool>("--plan-cache-evidence")
        {
            Description = ReportOutput.PlanCacheEvidenceOptionDescription,
            DefaultValueFactory = _ => false,
        };

        var confidenceOption = new Option<string>("--confidence")
        {
            Description = ReportOutput.ConfidenceOptionDescription,
            DefaultValueFactory = _ => "high",
        };

        var fetchSqlFromTablesOption = new Option<bool>("--fetch-sql-from-tables")
        {
            Description = ReportOutput.FetchSqlFromTablesOptionDescription,
            DefaultValueFactory = _ => false,
        };

        var verbosityOption = new Option<string>("--verbosity")
        {
            Description = ReportOutput.VerbosityOptionDescription,
            DefaultValueFactory = _ => "brief",
        };

        var strictOption = new Option<bool>("--strict")
        {
            Description = ReportOutput.StrictOptionDescription,
            DefaultValueFactory = _ => false,
        };

        var description = "Connect to a live SQL Server database, read its catalog from engine metadata, and scan every readable module across all 234 rules in 11 families: conversions and silent write loss, sargability, lineage metrics, catalog and constraint state, plan shape, control flow and transactions, dynamic SQL, code quality and security, index design, query anti-patterns, triggers and cross-module correctness.\n\nOptions:\n"
            + $"  --format <format> (default: text) - {ReportOutput.FormatOptionDescription}\n"
            + $"  --confidence <confidence> (default: high) - {ReportOutput.ConfidenceOptionDescription}\n"
            + $"  --plan-cache-evidence (default: off) - {ReportOutput.PlanCacheEvidenceOptionDescription}\n"
            + $"  --fetch-sql-from-tables (default: off) - {ReportOutput.FetchSqlFromTablesOptionDescription}\n"
            + $"  --verbosity <verbosity> (default: brief) - {ReportOutput.VerbosityOptionDescription}\n"
            + $"  --strict (default: off) - {ReportOutput.StrictOptionDescription}\n"
            + $"  --output <output> - {ReportOutput.OutputOptionDescription}";

        var command = new Command("scan-db", description)
        {
            connectionStringArgument,
            formatOption,
            planCacheEvidenceOption,
            confidenceOption,
            fetchSqlFromTablesOption,
            verbosityOption,
            strictOption,
            outputOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var connectionString = parseResult.GetValue(connectionStringArgument)!;
            var planCacheEvidence = parseResult.GetValue(planCacheEvidenceOption);
            var fetchSqlFromTables = parseResult.GetValue(fetchSqlFromTablesOption);
            var strict = parseResult.GetValue(strictOption);
            var options = new ReportOptions(
                parseResult.GetValue(formatOption)!,
                parseResult.GetValue(confidenceOption)!,
                parseResult.GetValue(outputOption),
                parseResult.GetValue(verbosityOption)!);
            return await RunAsync(connectionString, new ScanFlags(planCacheEvidence, fetchSqlFromTables, strict), options, Console.Out, Console.Error, cancellationToken);
        });

        return command;
    }

    internal static async Task<int> RunAsync(
        string connectionString, ScanFlags flags, ReportOptions options, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        if (!ReportOutput.TryParseFormat(options.Format, out var reportFormat))
        {
            await stderr.WriteLineAsync(ReportOutput.UnknownFormatMessage(options.Format));
            return 1;
        }

        if (!ReportOutput.TryParseConfidence(options.Confidence, out var minimumConfidence))
        {
            await stderr.WriteLineAsync(ReportOutput.UnknownConfidenceMessage(options.Confidence));
            return 1;
        }

        if (!ReportOutput.TryParseVerbosity(options.Verbosity, out var verbosity))
        {
            await stderr.WriteLineAsync(ReportOutput.UnknownVerbosityMessage(options.Verbosity));
            return 1;
        }

        var progress = new TextWriterScanProgress(stderr);
        var overall = Stopwatch.StartNew();

        LiveScanResult result;
        try
        {
            result = await LiveScanRunner.RunAsync(connectionString, flags.IncludePlanCacheEvidence, minimumConfidence, progress, flags.FetchSqlFromTables, cancellationToken);
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            await stderr.WriteLineAsync($"error: could not scan the live database: {ex.Message}");
            return 1;
        }

        await WarnOnParseHealthAsync(result, stderr);

        string content;
        using (var renderStage = progress.Begin("rendering report"))
        {
            content = reportFormat switch
            {
                ReportFormat.Sarif => SarifReportWriter.Write(result.Report),
                ReportFormat.Json => JsonSerializer.Serialize(result, JsonOptions),
                _ => ReadableLiveScanWriter.Write(result, ReadableLiveScanWriter.DescribeTarget(connectionString), ReportOutput.ToStyle(reportFormat), verbosity),
            };
            renderStage.Complete($"{options.Format}, {content.Length:N0} chars");
        }

        if (!ReportOutput.Emit(content, options.OutputPath, stdout, stderr))
        {
            return 1;
        }

        progress.Done(overall.Elapsed);

        if (result.LineageParity.Mismatches.Count != 0)
        {
            return 1;
        }

        return flags.Strict && ReportOutput.HasCoverageGaps(result.Report) ? 1 : 0;
    }

    private static async Task WarnOnParseHealthAsync(LiveScanResult result, TextWriter stderr)
    {
        foreach (var file in result.Report.ParseHealth.Files)
        {
            foreach (var error in file.Errors)
            {
                await stderr.WriteLineAsync($"warning: '{file.Path}' failed to parse: line {error.Line}: {error.Message}");
            }

            foreach (var unanalyzed in file.UnanalyzedBatches)
            {
                var what = unanalyzed.ObjectName is { } name ? $"{unanalyzed.Kind} '{name}'" : "an unidentified object";
                await stderr.WriteLineAsync(
                    $"warning: '{unanalyzed.SourcePath}':{unanalyzed.StartLine} - a batch failed to parse and was dropped; " +
                    $"{what} received zero analysis.");
            }
        }
    }
}
