using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using SilentScan.Live;
using SilentScan.Live.Catalog;

namespace SilentScan.Cli.Commands;

/// <summary>
/// `silentscan scan-db &lt;connection-string&gt;` — connects to a live SQL Server database and
/// builds its catalog directly from engine metadata (<c>sys.tables</c>/<c>sys.columns</c>/
/// <c>sys.indexes</c>) rather than inferring it from DDL text: types, per-column collations,
/// and the indexed flag are all facts read from the engine, not guesses. Issues metadata
/// <c>SELECT</c>s only - nothing is ever executed against the connected database.
/// Module bodies (views/procs/functions/triggers) feeding through the Lineage/Predicates/Rules
/// pipeline for actual findings is a separate stage; today this command reports the catalog
/// connected and read cleanly, plus honest accounting of anything it could not map.
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

        var command = new Command("scan-db", "Connect to a live SQL Server database and read its catalog directly from engine metadata.")
        {
            connectionStringArgument,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var connectionString = parseResult.GetValue(connectionStringArgument)!;
            return await RunAsync(connectionString, Console.Out, Console.Error, cancellationToken);
        });

        return command;
    }

    internal static async Task<int> RunAsync(string connectionString, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        try
        {
            var catalog = await new LiveCatalogReader(connectionString).ReadAsync(cancellationToken);
            var summary = LiveCatalogSummary.From(catalog);
            await stdout.WriteLineAsync(JsonSerializer.Serialize(summary, JsonOptions));
            return 0;
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            await stderr.WriteLineAsync($"error: could not read the live catalog: {ex.Message}");
            return 1;
        }
    }
}
