using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Verify.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Verify.Oracle;

public sealed class ProcedureResultColumnReader
{
    private readonly SqlServerOptions _options;

    public ProcedureResultColumnReader(SqlServerOptions options)
    {
        _options = options;
    }

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
