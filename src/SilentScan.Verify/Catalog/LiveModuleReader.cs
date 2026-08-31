using Microsoft.Data.SqlClient;
using SilentScan.Core.Diagnostics;

namespace SilentScan.Verify.Catalog;

public sealed class LiveModuleReader
{
    private readonly string _connectionString;

    public LiveModuleReader(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<LiveModuleReadResult> ReadAsync(IScanStage? stage = null, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var modules = await ReadReadableModulesAsync(connection, stage, cancellationToken);
        var encrypted = await ReadEncryptedModulesAsync(connection, cancellationToken);
        var clr = await ReadClrModulesAsync(connection, cancellationToken);
        var nonStandard = await ReadNonStandardModuleTypesAsync(connection, cancellationToken);
        var numberedProcedureBodies = await ReadNumberedProcedureBodiesBeyondFirstAsync(connection, cancellationToken);

        return new LiveModuleReadResult(modules, [.. encrypted, .. clr, .. nonStandard, .. numberedProcedureBodies]);
    }

    private static async Task<List<LiveModule>> ReadReadableModulesAsync(SqlConnection connection, IScanStage? stage, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.name AS schema_name, o.name AS object_name, o.type AS object_type, m.definition, m.uses_quoted_identifier, m.uses_ansi_nulls,
                   m.is_schema_bound, m.is_recompiled, m.uses_database_collation
            FROM sys.sql_modules m
            JOIN sys.objects o ON o.object_id = m.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0
              AND m.definition IS NOT NULL
              AND o.type IN ('V', 'P', 'FN', 'TF', 'IF', 'TR')
            ORDER BY s.name, o.name;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var modules = new List<LiveModule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var schemaName = reader.GetString(0);
            var objectName = reader.GetString(1);
            stage?.Advance(currentItem: $"{schemaName}.{objectName}");
            modules.Add(new LiveModule(
                SchemaName: schemaName,
                ObjectName: objectName,
                ObjectTypeCode: reader.GetString(2).Trim(),
                Definition: reader.GetString(3),

                UsesQuotedIdentifier: reader.GetBoolean(4),

                UsesAnsiNulls: reader.GetBoolean(5),

                IsSchemaBound: reader.GetBoolean(6),
                IsRecompiled: reader.GetBoolean(7),
                UsesDatabaseCollation: reader.GetBoolean(8)));
        }

        return modules;
    }

    private static async Task<List<UnanalyzableModule>> ReadEncryptedModulesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {

        const string sql = """
            SELECT s.name AS schema_name, o.name AS object_name, o.type AS object_type
            FROM sys.sql_modules m
            JOIN sys.objects o ON o.object_id = m.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0 AND m.definition IS NULL
            ORDER BY s.name, o.name;
            """;

        return await ReadUnanalyzableAsync(connection, sql, UnanalyzableModuleReason.Encrypted, cancellationToken);
    }

    private static async Task<List<UnanalyzableModule>> ReadClrModulesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {

        const string sql = """
            SELECT s.name AS schema_name, o.name AS object_name, o.type AS object_type
            FROM sys.assembly_modules am
            JOIN sys.objects o ON o.object_id = am.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0
            ORDER BY s.name, o.name;
            """;

        return await ReadUnanalyzableAsync(connection, sql, UnanalyzableModuleReason.ClrAssemblyModule, cancellationToken);
    }

    private static async Task<List<UnanalyzableModule>> ReadNonStandardModuleTypesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.name AS schema_name, o.name AS object_name, o.type AS object_type
            FROM sys.sql_modules m
            JOIN sys.objects o ON o.object_id = m.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0
              AND m.definition IS NOT NULL
              AND o.type NOT IN ('V', 'P', 'FN', 'TF', 'IF', 'TR')
            ORDER BY s.name, o.name;
            """;

        return await ReadUnanalyzableAsync(connection, sql, UnanalyzableModuleReason.NonStandardModuleType, cancellationToken);
    }

    private static async Task<List<UnanalyzableModule>> ReadNumberedProcedureBodiesBeyondFirstAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.name AS schema_name, o.name AS object_name, np.procedure_number
            FROM sys.numbered_procedures np
            JOIN sys.objects o ON o.object_id = np.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0
              AND np.procedure_number > 1
            ORDER BY s.name, o.name, np.procedure_number;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var results = new List<UnanalyzableModule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new UnanalyzableModule(
                SchemaName: reader.GetString(0),
                ObjectName: $"{reader.GetString(1)};{reader.GetInt16(2)}",
                ObjectTypeCode: "P",
                Reason: UnanalyzableModuleReason.NumberedProcedureBody));
        }

        return results;
    }

    private static async Task<List<UnanalyzableModule>> ReadUnanalyzableAsync(
        SqlConnection connection, string sql, UnanalyzableModuleReason reason, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateReadOnlyCommand(sql);

        var results = new List<UnanalyzableModule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new UnanalyzableModule(
                SchemaName: reader.GetString(0),
                ObjectName: reader.GetString(1),
                ObjectTypeCode: reader.GetString(2).Trim(),
                Reason: reason));
        }

        return results;
    }
}

public sealed record LiveModuleReadResult(IReadOnlyList<LiveModule> Modules, IReadOnlyList<UnanalyzableModule> Unanalyzable);
