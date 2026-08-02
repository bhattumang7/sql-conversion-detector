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
/// Supports JSON (default) or SARIF output, matching <c>scan</c>'s surface.
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
            Description = "Output format: json (default) or sarif.",
            DefaultValueFactory = _ => "json",
        };

        var command = new Command("scan-db", "Connect to a live SQL Server database, read its catalog from engine metadata, and scan every readable module for sargability findings.")
        {
            connectionStringArgument,
            formatOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var connectionString = parseResult.GetValue(connectionStringArgument)!;
            var format = parseResult.GetValue(formatOption)!;
            return await RunAsync(connectionString, format, Console.Out, Console.Error, cancellationToken);
        });

        return command;
    }

    internal static async Task<int> RunAsync(string connectionString, string format, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (format is not ("json" or "sarif"))
        {
            await stderr.WriteLineAsync($"error: unknown --format '{format}' (expected 'json' or 'sarif')");
            return 1;
        }

        LiveScanResult result;
        try
        {
            result = await LiveScanRunner.RunAsync(connectionString, cancellationToken);
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            await stderr.WriteLineAsync($"error: could not scan the live database: {ex.Message}");
            return 1;
        }

        if (format == "sarif")
        {
            await stdout.WriteLineAsync(SarifReportWriter.Write(result.Report));
        }
        else
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions));
        }

        // Non-zero on a P0 lineage bug (CLAUDE.md: "any mismatch is a P0 lineage bug") in
        // addition to a hard connection/read failure - findings built on a type the pipeline
        // got demonstrably wrong should never report a clean exit code.
        return result.LineageParityMismatches.Count == 0 ? 0 : 1;
    }
}
