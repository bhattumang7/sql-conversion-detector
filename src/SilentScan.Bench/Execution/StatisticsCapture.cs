using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SilentScan.Bench.Execution;

public static partial class StatisticsCapture
{
    [GeneratedRegex(@"logical reads (\d+)")]
    private static partial Regex LogicalReadsPattern();

    [GeneratedRegex(@"CPU time = (\d+) ms,\s*elapsed time = (\d+) ms\.")]
    private static partial Regex ExecutionTimePattern();

    public static async Task<QueryStatistics> CaptureAsync(SqlConnection connection, string query, CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        void Handler(object? sender, SqlInfoMessageEventArgs e) => messages.Add(e.Message);

        connection.InfoMessage += Handler;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = query;
            command.CommandTimeout = 120;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {

            }

            while (await reader.NextResultAsync(cancellationToken))
            {

            }
        }
        finally
        {
            connection.InfoMessage -= Handler;
        }

        return Parse(messages);
    }

    private static QueryStatistics Parse(IReadOnlyList<string> messages)
    {
        long logicalReads = 0;
        long cpuMs = 0;
        long elapsedMs = 0;

        foreach (var message in messages)
        {
            foreach (Match match in LogicalReadsPattern().Matches(message))
            {
                logicalReads += long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            }

            var executionMatches = ExecutionTimePattern().Matches(message);
            if (executionMatches.Count == 0)
            {
                continue;
            }

            var last = executionMatches[^1];
            cpuMs = long.Parse(last.Groups[1].Value, CultureInfo.InvariantCulture);
            elapsedMs = long.Parse(last.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        return new QueryStatistics(logicalReads, cpuMs, elapsedMs);
    }
}
