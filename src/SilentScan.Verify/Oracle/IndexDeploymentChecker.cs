using Microsoft.Data.SqlClient;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Confirms a finding's own column actually has a deployed index with that column as the
/// leading key, before the plan-shape signal (absence of GetRangeThroughConvert) is trusted to
/// confirm a ScanForced or RangeSeek verdict. Both those verdicts imply "the engine has a usable
/// index it can't seek through" - if the index never deployed (a permission-grant statement or
/// an ordering dependency made an earlier CREATE INDEX batch fail, CLAUDE.md Verify: "deployment
/// is best-effort"), a trivial heap scan also lacks GetRangeThroughConvert, and would otherwise
/// silently confirm a verdict the environment never actually tested.
/// </summary>
public sealed class IndexDeploymentChecker
{
    private readonly SqlServerOptions _options;

    public IndexDeploymentChecker(SqlServerOptions options)
    {
        _options = options;
    }

    /// <summary>True if <paramref name="schemaQualifiedTable"/> has any non-heap index (clustered or nonclustered) whose leading key column is <paramref name="columnName"/>.</summary>
    public async Task<bool> HasLeadingKeyIndexAsync(
        string database, string schemaQualifiedTable, string columnName, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM sys.index_columns ic
            JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE ic.object_id = OBJECT_ID(@objectName)
              AND ic.key_ordinal = 1
              AND ic.is_included_column = 0
              AND i.type IN (1, 2)
              AND c.name = @columnName;
            """;

        await using var connection = new SqlConnection(_options.BuildConnectionString(database));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@objectName", schemaQualifiedTable);
        command.Parameters.AddWithValue("@columnName", columnName);

        var count = (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
        return count > 0;
    }
}
