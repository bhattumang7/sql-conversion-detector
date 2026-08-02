using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
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

        var command = new Command("scan-db", "Connect to a live SQL Server database, read its catalog from engine metadata, and scan every readable module for sargability findings.")
        {
            connectionStringArgument,
            formatOption,
            planCacheEvidenceOption,
            outputOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var connectionString = parseResult.GetValue(connectionStringArgument)!;
            var format = parseResult.GetValue(formatOption)!;
            var planCacheEvidence = parseResult.GetValue(planCacheEvidenceOption);
            var output = parseResult.GetValue(outputOption);
            return await RunAsync(connectionString, format, planCacheEvidence, Console.Out, Console.Error, cancellationToken, output);
        });

        return command;
    }

    internal static async Task<int> RunAsync(
        string connectionString, string format, bool includePlanCacheEvidence, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken, string? outputPath = null)
    {
        if (!ReportOutput.TryParseFormat(format, out var reportFormat))
        {
            await stderr.WriteLineAsync(ReportOutput.UnknownFormatMessage(format));
            return 1;
        }

        LiveScanResult result;
        try
        {
            result = await LiveScanRunner.RunAsync(connectionString, includePlanCacheEvidence, cancellationToken);
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            await stderr.WriteLineAsync($"error: could not scan the live database: {ex.Message}");
            return 1;
        }

        var content = reportFormat switch
        {
            ReportFormat.Sarif => SarifReportWriter.Write(result.Report),
            ReportFormat.Json => JsonSerializer.Serialize(result, JsonOptions),
            _ => ReadableLiveScanWriter.Write(result, ReadableLiveScanWriter.DescribeTarget(connectionString), ReportOutput.ToStyle(reportFormat)),
        };

        if (!ReportOutput.Emit(content, outputPath, stdout, stderr))
        {
            return 1;
        }

        // Non-zero on a P0 lineage bug (CLAUDE.md: "any mismatch is a P0 lineage bug") in
        // addition to a hard connection/read failure - findings built on a type the pipeline
        // got demonstrably wrong should never report a clean exit code.
        return result.LineageParityMismatches.Count == 0 ? 0 : 1;
    }
}
