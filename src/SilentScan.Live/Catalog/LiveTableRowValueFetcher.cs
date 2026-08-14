using Microsoft.Data.SqlClient;
using SilentScan.Core.Predicates.DynamicSqlValue;
using SilentScan.Verify.Catalog;

namespace SilentScan.Live.Catalog;

/// <summary>
/// The <c>scan-db --fetch-sql-from-tables</c> live implementation of
/// <see cref="ILiveRowValueFetcher"/> - reads up to N real distinct values of one column from
/// one user table, filtered by whatever literal-equality conjuncts the dynamic-SQL engine's own
/// WHERE-clause analysis (<c>DynamicSqlTransfer.TryExtractLiteralEqualityKeys</c>) could
/// statically recognize (possibly none, meaning every distinct value is fetched). Every table/
/// column name comes from this project's own catalog (never user input) and is bracket-quoted
/// the same way every other live query in this project already is; every literal key VALUE -
/// genuinely untrusted, lifted from source text this scanner is analyzing - is bound as a real
/// ADO.NET parameter, never string-interpolated. The outer query still goes through
/// <see cref="LiveReadOnlyGuard.CreateReadOnlyCommand"/> like every other live read in this
/// project (SELECT-only, <see cref="LiveReadOnlyGuard.DefaultCommandTimeoutSeconds"/> applied),
/// so it can never be anything but a SELECT even if this class had a bug.
///
/// <see cref="ILiveRowValueFetcher.TryFetchDistinctValues"/> is a synchronous contract (the
/// dynamic-SQL engine's own traversal is fully synchronous, with no async anywhere in it) -
/// blocking on the async ADO.NET call here is safe specifically because this runs inside
/// `silentscan`, a console CLI with no SynchronizationContext to deadlock against, not a pattern
/// used anywhere else in this codebase (everywhere else stays async end to end).
///
/// Caches every (table, column, keys, maxRows) result for the lifetime of one scan: the
/// dynamic-SQL engine's own fixpoint solver re-evaluates state-mutating statements across
/// several rounds before it converges, so the SAME call site's fetch would otherwise round-trip
/// to the database several times over for an identical answer.
///
/// <see cref="SilentScan.Core.Reporting.ScanReportBuilder"/> scans modules in parallel (PLINQ), and every module shares
/// this ONE fetcher/connection instance - a plain, non-MARS <see cref="SqlConnection"/> throws
/// "does not support MultipleActiveResultSets" the moment two threads issue commands on it at
/// the same time. <see cref="_gate"/> serializes the whole check-cache/fetch/populate-cache
/// section per instance so only one command is ever in flight on the shared connection;
/// correctness, not fetch throughput, is what matters here, and results are cached anyway.
/// </summary>
public sealed class LiveTableRowValueFetcher(SqlConnection connection) : ILiveRowValueFetcher
{
    private readonly Dictionary<string, IReadOnlyList<string>?> _cache = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public IReadOnlyList<string>? TryFetchDistinctValues(
        string tableQualifiedName, string selectColumn, IReadOnlyList<(string Column, string LiteralValue)> equalityKeys, int maxRows)
    {
        var cacheKey = BuildCacheKey(tableQualifiedName, selectColumn, equalityKeys, maxRows);
        lock (_gate)
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var result = FetchAsync(tableQualifiedName, selectColumn, equalityKeys, maxRows).GetAwaiter().GetResult();
            _cache[cacheKey] = result;
            return result;
        }
    }

    private static string BuildCacheKey(
        string tableQualifiedName, string selectColumn, IReadOnlyList<(string Column, string LiteralValue)> equalityKeys, int maxRows) =>
        string.Join(
            '',
            new[] { tableQualifiedName, selectColumn, maxRows.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                .Concat(equalityKeys.Select(k => $"{k.Column}={k.LiteralValue}")));

    private async Task<IReadOnlyList<string>?> FetchAsync(
        string tableQualifiedName, string selectColumn, IReadOnlyList<(string Column, string LiteralValue)> equalityKeys, int maxRows)
    {
        var parts = tableQualifiedName.Split('.', 2);
        if (parts.Length != 2 || maxRows <= 0)
        {
            return null;
        }

        var whereClause = equalityKeys.Count == 0
            ? string.Empty
            : "WHERE " + string.Join(" AND ", equalityKeys.Select((k, i) => $"{Bracket(k.Column)} = @p{i}"));
        var sql = $"SELECT DISTINCT TOP ({maxRows}) {Bracket(selectColumn)} FROM {Bracket(parts[0])}.{Bracket(parts[1])} {whereClause};";

        try
        {
            await using var command = connection.CreateReadOnlyCommand(sql);
            for (var i = 0; i < equalityKeys.Count; i++)
            {
                command.Parameters.AddWithValue($"@p{i}", equalityKeys[i].LiteralValue);
            }

            var values = new List<string>(maxRows);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!await reader.IsDBNullAsync(0))
                {
                    values.Add(reader.GetValue(0)?.ToString() ?? string.Empty);
                }
            }

            return values.Count > 0 ? values : null;
        }
        catch (SqlException)
        {
            // A column type this project's live catalog can't compare (xml, a CLR UDT) or any
            // other read failure - decline rather than propagate, matching every other live
            // reader's own "conditions of the target, not a bug in this tool" stance.
            return null;
        }
    }

    private static string Bracket(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}
