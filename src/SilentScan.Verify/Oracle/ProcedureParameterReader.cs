using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Verify.Catalog;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Resolves a stored procedure's own parameter list from <c>sys.parameters</c> - the
/// INSERT...EXEC fence probe (<see cref="TvfFenceProbeBuilder.BuildInsertExecProbe"/>) needs to
/// call the executed procedure itself, and a procedure with required parameters rejects a bare
/// <c>EXEC dbo.Proc</c> exactly like a function rejects a bare reference (the same problem
/// <see cref="FunctionParameterReader"/> solves for functions - not reused directly here since
/// its own <c>IsKnownObjectAsync</c> restricts "zero parameters is real, not just absent" to
/// <c>sys.objects.type IN ('FN','TF','IF','FT')</c>, which a procedure's <c>'P'</c> never
/// matches).
/// </summary>
public sealed class ProcedureParameterReader
{
    private readonly SqlServerOptions _options;

    public ProcedureParameterReader(SqlServerOptions options)
    {
        _options = options;
    }

    /// <summary>Returns the procedure's parameter types in declaration order, or null when at least one parameter's type has no T-SQL rendering (an OUTPUT parameter or a table-valued parameter, since neither belongs in a plain positional argument list a dummy-value probe can supply) - a genuinely zero-parameter procedure returns an empty, non-null list.</summary>
    public async Task<IReadOnlyList<SqlType>?> TryGetParameterTypesAsync(
        string database, string qualifiedName, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT ty.name AS type_name, p.max_length, p.precision, p.scale, p.is_output, p.is_readonly
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
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var isOutput = reader.GetBoolean(4);
            var isTableValued = reader.GetBoolean(5);
            if (isOutput || isTableValued)
            {
                // An OUTPUT parameter or a table-valued parameter can't be supplied as a plain
                // positional dummy value the way an ordinary IN parameter can - the probe would
                // need EXEC ... @p OUTPUT / a real table variable, out of scope for this
                // compile-only fence probe. Reported as unrenderable rather than guessed.
                return null;
            }

            var typeName = reader.GetString(0);
            var maxLength = reader.GetInt16(1);
            var precision = reader.GetByte(2);
            var scale = reader.GetByte(3);

            var type = LiveTypeMapper.BuildType(typeName, maxLength, precision, scale, collationName: null);
            if (type is null)
            {
                return null;
            }

            types.Add(type);
        }

        return types;
    }
}
