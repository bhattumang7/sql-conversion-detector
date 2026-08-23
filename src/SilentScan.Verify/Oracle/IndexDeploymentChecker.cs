using Microsoft.Data.SqlClient;

namespace SilentScan.Verify.Oracle;

public sealed class IndexDeploymentChecker
{
    private readonly SqlServerOptions _options;

    public IndexDeploymentChecker(SqlServerOptions options)
    {
        _options = options;
    }

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

        }
    }

    private static string BracketQualified(string qualifiedName)
    {
        var parts = qualifiedName.Split('.', 2);
        return parts.Length == 2 ? $"{Bracket(parts[0])}.{Bracket(parts[1])}" : Bracket(parts[0]);
    }

    private static string Bracket(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
}
