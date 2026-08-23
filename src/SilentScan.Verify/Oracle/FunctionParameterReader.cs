using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Verify.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Verify.Oracle;

public sealed class FunctionParameterReader
{
    private readonly SqlServerOptions _options;

    public FunctionParameterReader(SqlServerOptions options)
    {
        _options = options;
    }

    public async Task<IReadOnlyList<SqlType>?> TryGetParameterTypesAsync(
        string database, string qualifiedName, CancellationToken cancellationToken = default)
    {

        const string sql = """
            SELECT ty.name AS type_name, p.max_length, p.precision, p.scale
            FROM sys.parameters p
            JOIN sys.types ty ON ty.user_type_id = p.user_type_id
            WHERE p.object_id = OBJECT_ID(@objectName)
              AND p.parameter_id > 0
            ORDER BY p.parameter_id;
            """;

        await using var connection = new SqlConnection(_options.BuildConnectionString(database));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@objectName", qualifiedName);

        var types = new List<SqlType>();
        var allTypesRendered = true;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var typeName = reader.GetString(0);
                var maxLength = reader.GetInt16(1);
                var precision = reader.GetByte(2);
                var scale = reader.GetByte(3);

                var type = LiveTypeMapper.BuildType(typeName, maxLength, precision, scale, collationName: null);
                if (type is null)
                {

                    allTypesRendered = false;
                }
                else
                {
                    types.Add(type);
                }
            }
        }

        if (!allTypesRendered)
        {
            return null;
        }

        return types.Count == 0 && !await IsKnownObjectAsync(connection, qualifiedName, cancellationToken)
            ? null
            : types;
    }

    private static async Task<bool> IsKnownObjectAsync(SqlConnection connection, string qualifiedName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type FROM sys.objects WHERE object_id = OBJECT_ID(@objectName);";
        command.Parameters.AddWithValue("@objectName", qualifiedName);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        var typeCode = (result as string)?.Trim();
        return typeCode is "FN" or "TF" or "IF" or "FT";
    }
}
