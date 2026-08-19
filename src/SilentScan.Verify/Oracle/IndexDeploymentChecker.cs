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

    /// <summary>The name of a non-heap index (clustered or nonclustered) whose leading key column is <paramref name="columnName"/>, if any - lets a caller scope a plan-XML check to that specific index rather than asking "is there an Index Seek anywhere in this plan."</summary>
    public async Task<string?> TryGetLeadingKeyIndexNameAsync(
        string database, string schemaQualifiedTable, string columnName, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (1) i.name
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

        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>
    /// Roadmap Phase E3: the common case in real-world corpora is a ScanForced/RangeSeek
    /// finding on a column the corpus's own DDL never indexed at all - previously this meant
    /// the RangeSeek-vs-ScanForced plan-SHAPE claim (as opposed to the CONVERT_IMPLICIT claim,
    /// which needs no index) could never be oracle-tested for the majority of real findings.
    /// Deploys a single-column NONCLUSTERED index for this probe only, empty table, no rows
    /// touched - a CREATE INDEX is DDL, not a corpus DML/procedure-body execution CLAUDE.md's
    /// hard scope forbids. Returns null (never throws) when the column's own type can't be
    /// indexed at all (MAX-length string, XML, ...) - the caller falls back to the same
    /// ConfirmedUnindexed outcome an undeployed corpus index already produces, not a crash.
    /// </summary>
    public async Task<string?> TryDeployScratchIndexAsync(
        string database, string schemaQualifiedTable, string columnName, CancellationToken cancellationToken = default)
    {
        var indexName = $"IX_SilentScanScratch_{Guid.NewGuid():N}";

        await using var connection = new SqlConnection(_options.BuildConnectionString(database));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE INDEX {Bracket(indexName)} ON {BracketQualified(schemaQualifiedTable)} ({Bracket(columnName)});";

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return indexName;
        }
        catch (SqlException)
        {
            return null;
        }
    }

    /// <summary>Best-effort cleanup so one probe's scratch index never lingers to affect a later probe on the same column, or the environment-parity gate's own sys.columns diff - never throws, since a probe that already failed (ProbeFailed) has nothing further to report from a cleanup failure too.</summary>
    public async Task DropIndexIfExistsAsync(
        string database, string schemaQualifiedTable, string indexName, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_options.BuildConnectionString(database));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = @indexName AND object_id = OBJECT_ID(@objectName)) " +
            $"DROP INDEX {Bracket(indexName)} ON {BracketQualified(schemaQualifiedTable)};";
        command.Parameters.AddWithValue("@indexName", indexName);
        command.Parameters.AddWithValue("@objectName", schemaQualifiedTable);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException)
        {
            // Best-effort - the disposable database is dropped whole at the end of the repo's
            // verify run regardless, so a cleanup failure here never leaks state across repos.
        }
    }

    private static string BracketQualified(string qualifiedName)
    {
        var parts = qualifiedName.Split('.', 2);
        return parts.Length == 2 ? $"{Bracket(parts[0])}.{Bracket(parts[1])}" : Bracket(parts[0]);
    }

    private static string Bracket(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}
