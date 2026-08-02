using Microsoft.Data.SqlClient;

namespace SilentScan.Live.Catalog;

/// <summary>
/// Reads every readable T-SQL module body (view/procedure/scalar or table-valued function/
/// trigger definition) from <c>sys.sql_modules</c>, for the live analysis pipeline to parse and
/// run through the same Lineage/Predicates/Rules passes file-mode scanning uses. Issues a
/// metadata <c>SELECT</c> only - module bodies are read as text, never executed.
/// </summary>
public sealed class LiveModuleReader
{
    private readonly string _connectionString;

    public LiveModuleReader(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<LiveModule>> ReadAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT s.name AS schema_name, o.name AS object_name, o.type AS object_type, m.definition
            FROM sys.sql_modules m
            JOIN sys.objects o ON o.object_id = m.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0
              AND m.definition IS NOT NULL
              AND o.type IN ('V', 'P', 'FN', 'TF', 'IF', 'TR')
            ORDER BY s.name, o.name;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var modules = new List<LiveModule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            modules.Add(new LiveModule(
                SchemaName: reader.GetString(0),
                ObjectName: reader.GetString(1),
                ObjectTypeCode: reader.GetString(2).Trim(),
                Definition: reader.GetString(3)));
        }

        return modules;
    }
}
