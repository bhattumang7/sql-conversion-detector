using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Reporting.Sarif;
using SilentScan.Live;

namespace SilentScan.Cli.Commands;

public static class ScanDbCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
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
            Description = "Also read the live plan cache and rank findings by whether they are actually observed converting in a real cached plan, with execution counts. Requires VIEW SERVER STATE; off by default.",
            DefaultValueFactory = _ => false,
        };

        var confidenceOption = new Option<string>("--confidence")
        {
            Description = ReportOutput.ConfidenceOptionDescription,
            DefaultValueFactory = _ => "high",
        };

        var fetchSqlFromTablesOption = new Option<bool>("--fetch-sql-from-tables")
        {
            Description = "Also fetch the real value(s) of dynamic SQL text stored in a table (e.g. SELECT @sql = Definition FROM dbo.Templates WHERE Name = 'X') instead of leaving it unanalyzable - narrowed by whatever literal WHERE conditions can be pushed down, every distinct value analyzed as its own candidate when more than one matches. Reads real row content, not just catalog metadata - off by default.",
            DefaultValueFactory = _ => false,
        };

        var verbosityOption = new Option<string>("--verbosity")
        {
            Description = ReportOutput.VerbosityOptionDescription,
            DefaultValueFactory = _ => "brief",
        };

        var command = new Command("scan-db", "Connect to a live SQL Server database, read its catalog from engine metadata, and scan every readable module for implicit-conversion, MSTVF-as-fence, and scalar-UDF findings.")
        {
            connectionStringArgument,
            formatOption,
            planCacheEvidenceOption,
            confidenceOption,
            fetchSqlFromTablesOption,
            verbosityOption,
            outputOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var connectionString = parseResult.GetValue(connectionStringArgument)!;
            var planCacheEvidence = parseResult.GetValue(planCacheEvidenceOption);
            var fetchSqlFromTables = parseResult.GetValue(fetchSqlFromTablesOption);
            var options = new ReportOptions(
                parseResult.GetValue(formatOption)!,
                parseResult.GetValue(confidenceOption)!,
                parseResult.GetValue(outputOption),
                parseResult.GetValue(verbosityOption)!);
            return await RunAsync(connectionString, planCacheEvidence, fetchSqlFromTables, options, Console.Out, Console.Error, cancellationToken);
        });

        return command;
    }

    internal static async Task<int> RunAsync(
        string connectionString, bool includePlanCacheEvidence, bool fetchSqlFromTables, ReportOptions options, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
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
            result = await LiveScanRunner.RunAsync(connectionString, includePlanCacheEvidence, minimumConfidence, progress, fetchSqlFromTables, cancellationToken);
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

        return result.LineageParity.Mismatches.Count == 0 ? 0 : 1;
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
