using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Verify.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Verify.Oracle;

/// <summary>
/// Resolves a scalar/table-valued function's own parameter list from <c>sys.parameters</c> -
/// needed because a finding whose column lives on an inline or multi-statement TVF
/// (First Responder Kit's <c>#missing_index_pretty</c>-adjacent shapes, DNN Platform's
/// <c>SplitStrings_CTE</c>/<c>ConvertListToTable</c>) cannot be probed by referencing the
/// function's bare name the way an ordinary table can: SQL Server rejects <c>SELECT 1 FROM
/// dbo.SplitStrings_CTE WHERE ...</c> outright with "Parameters were not supplied," a compile
/// error this scanner's own classification never depended on. Returns null for an ordinary table
/// (zero sys.parameters rows) so a caller can tell "not a function, needs no arguments" apart
/// from "a function with zero parameters" (an empty, non-null list) without a separate existence
/// check.
/// </summary>
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
        // sys.parameters has no collation_name column (unlike sys.columns) - a synthesized
        // probe argument is a dummy placeholder never itself compared against anything, so its
        // exact collation has no bearing on the CONVERT_IMPLICIT signal the probe is checking for
        // the FINDING's own column; passed as null to LiveTypeMapper.BuildType below, which just
        // omits an explicit COLLATE clause - syntactically valid, and irrelevant to a value never
        // compared against anything.
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
                    // A parameter type this scanner has no rendering for at all (xml, CLR UDT,
                    // ...) - the whole function can't be probed with a synthesized argument list,
                    // same as any other "not enough type information" case elsewhere in this
                    // codebase. Still drains the reader (keeps looping) rather than returning
                    // mid-iteration - MARS is off, so an open reader blocks the query this
                    // method's own caller needs run on the SAME connection right below.
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

        // The reader above is fully disposed by this point (its own `await using` block scope
        // already ended) - MARS is off by default, so a second command on the SAME connection
        // would otherwise fail with "There is already an open DataReader."
        return types.Count == 0 && !await IsKnownObjectAsync(connection, qualifiedName, cancellationToken)
            ? null
            : types;
    }

    /// <summary>
    /// Distinguishes "genuinely a zero-parameter function" from "not a function at all" (an
    /// ordinary table) - both produce zero sys.parameters rows, but only the first should still
    /// be probed as a function call.
    /// </summary>
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
