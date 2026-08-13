using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Reporting.Sarif;
using SilentScan.Live;

namespace SilentScan.Cli.Commands;

/// <summary>
/// `silentscan scan-db &lt;connection-string&gt;` — connects to a live SQL Server database,
/// builds its catalog directly from engine metadata (<c>sys.tables</c>/<c>sys.columns</c>/
/// <c>sys.indexes</c>) rather than inferring it from DDL text, and runs every readable module
/// body (views/procs/functions/triggers, from <c>sys.sql_modules</c>) through the same
/// Lineage/Predicates/Rules pipeline <c>scan</c> uses against parsed files. Types, per-column
/// collations, and the indexed flag are all facts read from the engine, not guesses. Issues
/// metadata <c>SELECT</c>s only - nothing is ever executed against the connected database.
/// Renders as readable text (default) or markdown, or as JSON or SARIF, matching <c>scan</c>'s surface.
/// </summary>
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

        var command = new Command("scan-db", "Connect to a live SQL Server database, read its catalog from engine metadata, and scan every readable module for sargability findings.")
        {
            connectionStringArgument,
            formatOption,
            planCacheEvidenceOption,
            confidenceOption,
            outputOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var connectionString = parseResult.GetValue(connectionStringArgument)!;
            var planCacheEvidence = parseResult.GetValue(planCacheEvidenceOption);
            var options = new ReportOptions(
                parseResult.GetValue(formatOption)!,
                parseResult.GetValue(confidenceOption)!,
                parseResult.GetValue(outputOption));
            return await RunAsync(connectionString, planCacheEvidence, options, Console.Out, Console.Error, cancellationToken);
        });

        return command;
    }

    internal static async Task<int> RunAsync(
        string connectionString, bool includePlanCacheEvidence, ReportOptions options, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
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

        // Progress goes to stderr, never stdout: a large-database scan runs for minutes and used
        // to print nothing at all until the finished report appeared, but the report itself is
        // piped (--format json/sarif), so the two streams must stay separate.
        var progress = new TextWriterScanProgress(stderr);
        var overall = Stopwatch.StartNew();

        LiveScanResult result;
        try
        {
            result = await LiveScanRunner.RunAsync(connectionString, includePlanCacheEvidence, minimumConfidence, progress, cancellationToken);
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            await stderr.WriteLineAsync($"error: could not scan the live database: {ex.Message}");
            return 1;
        }

        // Rendering is its own reported stage rather than silent tail-time. On a database with a
        // large finding count, serializing the report and writing it out is a substantial share
        // of total wall clock - measured at roughly a third of a real run - and reporting "done"
        // before it started meant the tool looked finished while it still had seconds of work
        // left, which is exactly the "takes ages to output anything" symptom.
        string content;
        using (var renderStage = progress.Begin("rendering report"))
        {
            content = reportFormat switch
            {
                ReportFormat.Sarif => SarifReportWriter.Write(result.Report),
                ReportFormat.Json => JsonSerializer.Serialize(result, JsonOptions),
                _ => ReadableLiveScanWriter.Write(result, ReadableLiveScanWriter.DescribeTarget(connectionString), ReportOutput.ToStyle(reportFormat)),
            };
            renderStage.Complete($"{options.Format}, {content.Length:N0} chars");
        }

        if (!ReportOutput.Emit(content, options.OutputPath, stdout, stderr))
        {
            return 1;
        }

        progress.Done(overall.Elapsed);

        // Non-zero on a P0 lineage bug in addition to a hard connection/read failure - findings
        // built on a type the pipeline got demonstrably wrong (verified against what the engine
        // computes for the object right now, not its possibly-stale cached metadata) should
        // never report a clean exit code. An uncompilable object or a merely-stale cache is a
        // condition of the scanned database, not a tool bug, so neither affects the exit code -
        // both still appear in the report, prominently.
        return result.LineageParity.Mismatches.Count == 0 ? 0 : 1;
    }
}
