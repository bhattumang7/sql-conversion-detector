using Microsoft.Data.SqlClient;
using SilentScan.Core.Predicates;
using SilentScan.Verify.Catalog;

namespace SilentScan.Live.Catalog;

public sealed class DanglingObjectReferenceChecker
{
    private readonly string _connectionString;

    public DanglingObjectReferenceChecker(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<DanglingObjectReferenceFinding>> CheckAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var candidates = await ReadCandidatesAsync(connection, cancellationToken);
        if (candidates.Count == 0)
        {
            return [];
        }

        var findings = new List<DanglingObjectReferenceFinding>();
        foreach (var candidate in candidates.OrderBy(c => c.ModuleQualifiedName, StringComparer.Ordinal).ThenBy(c => c.ReferencedEntityName, StringComparer.Ordinal))
        {
            if (await IsStillUnresolvedLiveAsync(connection, candidate, cancellationToken))
            {
                findings.Add(new DanglingObjectReferenceFinding(
                    candidate.ModuleQualifiedName, candidate.ModuleTypeDescription, candidate.ReferencedEntityName, candidate.ReferencedSchemaName,
                    SourcePath: candidate.ModuleQualifiedName, Line: 1, Column: 1));
            }
        }

        return findings;
    }

    private static async Task<List<Candidate>> ReadCandidatesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                OBJECT_SCHEMA_NAME(d.referencing_id) + N'.' + OBJECT_NAME(d.referencing_id) AS module_name,
                o.type_desc,
                d.referenced_entity_name,
                d.referenced_schema_name
            FROM sys.sql_expression_dependencies d
            JOIN sys.objects o ON o.object_id = d.referencing_id
            WHERE o.is_ms_shipped = 0
              AND o.type IN ('P', 'V', 'FN', 'IF', 'TF', 'TR')
              AND d.referenced_id IS NULL
              AND d.is_caller_dependent = 0
              AND d.is_ambiguous = 0
              AND d.referenced_server_name IS NULL
              AND d.referenced_database_name IS NULL
              AND d.referenced_class = 1
              AND NOT (o.type = 'TR' AND d.referenced_schema_name IS NULL AND d.referenced_entity_name IN (N'inserted', N'deleted'))
            ORDER BY module_name, d.referenced_entity_name;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var candidates = new List<Candidate>();
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new Candidate(
                ModuleQualifiedName: reader.GetString(0),
                ModuleTypeDescription: DescribeModuleType(reader.GetString(1)),
                ReferencedEntityName: reader.GetString(2),
                ReferencedSchemaName: await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3)));
        }

        return candidates;
    }

    private static async Task<bool> IsStillUnresolvedLiveAsync(SqlConnection connection, Candidate candidate, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT r.referenced_entity_name, r.referenced_schema_name
            FROM sys.dm_sql_referenced_entities(@moduleName, 'OBJECT') r
            WHERE r.referenced_id IS NULL
              AND r.is_caller_dependent = 0
              AND r.is_ambiguous = 0
              AND r.referenced_server_name IS NULL
              AND r.referenced_database_name IS NULL
              AND r.referenced_class = 1;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);
        command.Parameters.AddWithValue("@moduleName", candidate.ModuleQualifiedName);

        var stillUnresolved = false;
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var entityName = reader.GetString(0);
                var schemaName = await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1);
                if (string.Equals(entityName, candidate.ReferencedEntityName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(schemaName, candidate.ReferencedSchemaName, StringComparison.OrdinalIgnoreCase))
                {
                    stillUnresolved = true;
                }
            }
        }
        catch (SqlException ex) when (ex.Number == 2020)
        {
            stillUnresolved = false;
        }
        catch (SqlException ex) when (ex.Number == 208)
        {

            stillUnresolved = ex.Message.Contains(candidate.ReferencedEntityName, StringComparison.OrdinalIgnoreCase);
        }
        catch (SqlException ex) when (ex.Number == 207)
        {

            stillUnresolved = ex.Message.Contains(candidate.ReferencedEntityName, StringComparison.OrdinalIgnoreCase);
        }

        return stillUnresolved;
    }

    private static string DescribeModuleType(string typeDesc) => typeDesc switch
    {
        "SQL_STORED_PROCEDURE" => "stored procedure",
        "VIEW" => "view",
        "SQL_SCALAR_FUNCTION" => "scalar function",
        "SQL_INLINE_TABLE_VALUED_FUNCTION" => "inline table-valued function",
        "SQL_TABLE_VALUED_FUNCTION" => "table-valued function",
        "SQL_TRIGGER" => "trigger",
        _ => typeDesc,
    };

    private sealed record Candidate(string ModuleQualifiedName, string ModuleTypeDescription, string ReferencedEntityName, string? ReferencedSchemaName);
}
