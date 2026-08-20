using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Verify.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Describes a stored procedure's own first result set via
/// <c>sys.dm_exec_describe_first_result_set</c> - compile-only, exactly the technique CLAUDE.md
/// already sanctions for describing an object's shape without executing it
/// (<c>sys.dm_exec_describe_first_result_set</c> "parses, binds, and compiles the batch text it's
/// handed and returns result-set metadata without executing it at all"). The INSERT...EXEC fence
/// probe needs this because SQL Server validates the receiving table variable's column count
/// against the procedure's real output shape at compile time - a guessed shape would either
/// reject a real fence's own probe (false NotConfirmed) or, worse, silently compile against the
/// wrong shape and mask a genuine mismatch.
/// </summary>
public sealed class ProcedureResultColumnReader
{
    private readonly SqlServerOptions _options;

    public ProcedureResultColumnReader(SqlServerOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Returns the procedure's first result set's column types, or null when the engine could not
    /// describe it at all (a procedure with no result set, one whose shape varies by branch, or
    /// one this DMV otherwise refuses) - column NAMES are not needed, since the probe only ever
    /// declares a scratch table variable to receive into, never reads the values back out.
    /// </summary>
    public async Task<IReadOnlyList<SqlType>?> TryDescribeResultColumnsAsync(
        string database, string execProbeText, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT r.error_number, ty.name AS type_name, r.max_length, r.precision, r.scale
            FROM sys.dm_exec_describe_first_result_set(@probeText, NULL, 0) r
            LEFT JOIN sys.types ty ON ty.user_type_id = r.system_type_id
            ORDER BY r.column_ordinal;
            """;

        await using var connection = new SqlConnection(_options.BuildConnectionString(database));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@probeText", execProbeText);

        var columns = new List<SqlType>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!await reader.IsDBNullAsync(0, cancellationToken))
            {
                // The DMV reports a describe failure in-band (a non-null error_number) rather
                // than throwing - no row here carries real column data either way.
                return null;
            }

            if (await reader.IsDBNullAsync(1, cancellationToken))
            {
                return null;
            }

            var typeName = reader.GetString(1);
            var maxLength = reader.GetInt16(2);
            var precision = reader.GetByte(3);
            var scale = reader.GetByte(4);

            var type = LiveTypeMapper.BuildType(typeName, maxLength, precision, scale, collationName: null);
            if (type is null)
            {
                return null;
            }

            columns.Add(type);
        }

        return columns.Count == 0 ? null : columns;
    }
}
