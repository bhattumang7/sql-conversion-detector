using Microsoft.Data.SqlClient;

namespace SilentScan.Verify.Catalog;

/// <summary>
/// Reads every module body (view/procedure/scalar or table-valued function/trigger definition)
/// from <c>sys.sql_modules</c>, for the live analysis pipeline to parse and run through the
/// same Lineage/Predicates/Rules passes file-mode scanning uses. Issues metadata <c>SELECT</c>s
/// only - module bodies are read as text, never executed.
/// A module with no readable T-SQL body (CLR-assembly-backed, or created <c>WITH ENCRYPTION</c>)
/// is never silently dropped - it is returned in <see cref="LiveModuleReadResult.Unanalyzable"/>
/// instead, the same honesty policy <c>DynamicSqlSummary</c> already applies to unanalyzable
/// dynamic SQL call sites.
/// </summary>
public sealed class LiveModuleReader
{
    private readonly string _connectionString;

    public LiveModuleReader(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<LiveModuleReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var modules = await ReadReadableModulesAsync(connection, cancellationToken);
        var encrypted = await ReadEncryptedModulesAsync(connection, cancellationToken);
        var clr = await ReadClrModulesAsync(connection, cancellationToken);

        return new LiveModuleReadResult(modules, [.. encrypted, .. clr]);
    }

    private static async Task<List<LiveModule>> ReadReadableModulesAsync(SqlConnection connection, CancellationToken cancellationToken)
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
            modules.Add(new LiveModule(
                SchemaName: reader.GetString(0),
                ObjectName: reader.GetString(1),
                ObjectTypeCode: reader.GetString(2).Trim(),
                Definition: reader.GetString(3),
                // sys.sql_modules.uses_quoted_identifier captures the QUOTED_IDENTIFIER setting
                // at CREATE/ALTER time. Modules created under QI OFF use `"..."` as string
                // literals (the legacy `EXEC("...")` idiom); parsing them under the ScriptDOM
                // default (QI ON) turns those into unclosed quoted identifiers and drops the
                // batch. Threading the flag through keeps such modules analyzable rather than
                // silently misclassified as broken T-SQL.
                UsesQuotedIdentifier: reader.GetBoolean(4),
                // sys.sql_modules.uses_ansi_nulls - docs/detection-checklist.md Tier 1 "SET
                // options that silently disable plan features": ANSI_NULLS OFF, like
                // QUOTED_IDENTIFIER OFF, makes a filtered index/indexed view the module touches
                // unusable by the optimizer (oracle-confirmed directly, real seeded filtered
                // index, real SHOWPLAN_XML: PhysicalOp falls back to Table Scan under ANSI_NULLS
                // OFF). Baked in wholesale at CREATE/ALTER compile time, same as QUOTED_IDENTIFIER.
                UsesAnsiNulls: reader.GetBoolean(5),
                // sys.sql_modules.is_schema_bound/is_recompiled/uses_database_collation -
                // docs/detection-checklist.md "Small precise adds": WITH RECOMPILE (compiles
                // every call, invisible to the plan cache) and the table-valued-function
                // return-shape database-collation dependency (see ModuleCompileFlagFinding's own
                // doc comment for the oracle-confirmed scope, including why is_schema_bound is
                // read here purely to EXCLUDE the always-true schema-bound case from that second
                // finding, not to report on it directly).
                IsSchemaBound: reader.GetBoolean(6),
                IsRecompiled: reader.GetBoolean(7),
                UsesDatabaseCollation: reader.GetBoolean(8)));
        }

        return modules;
    }

    private static async Task<List<UnanalyzableModule>> ReadEncryptedModulesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        // WITH ENCRYPTION strips the readable definition from sys.sql_modules entirely - the
        // module still has a sys.sql_modules row (unlike a CLR module, which never gets one),
        // just with definition = NULL, so it is distinguished from the readable set by that
        // alone rather than by object type.
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
        // CLR-backed procs/scalar functions/table functions/aggregates never have a
        // sys.sql_modules row at all - sys.assembly_modules is their only catalog entry, and
        // it carries no T-SQL body to read (the implementation lives in the referenced
        // assembly). A predicate that CALLS one from ordinary T-SQL is still caught: it is an
        // unremarkable function-wrapped-column shape to Tier-1's syntactic scan, which does not
        // care what kind of function is on the other end.
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

/// <summary>Every module this pass could read a T-SQL body for, plus honest accounting of every one it could not.</summary>
public sealed record LiveModuleReadResult(IReadOnlyList<LiveModule> Modules, IReadOnlyList<UnanalyzableModule> Unanalyzable);
