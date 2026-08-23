using Microsoft.Data.SqlClient;
using SilentScan.Core.Predicates.DynamicSqlValue;
using SilentScan.Verify.Catalog;

namespace SilentScan.Live.Catalog;

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

            return null;
        }
    }

    private static string Bracket(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}
