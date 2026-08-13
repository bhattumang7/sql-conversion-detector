using Microsoft.Data.SqlClient;
using SilentScan.Verify.Catalog;

namespace SilentScan.Live.Catalog;

/// <summary>
/// Reads the column shape the engine computes RIGHT NOW for views and inline TVFs, via
/// <c>sys.dm_exec_describe_first_result_set</c> - the live-mode parity gate's ground truth,
/// replacing a bare diff against <c>sys.columns</c> (see <see cref="LiveLineageParityChecker"/>
/// for why: SQL Server snapshots a view's/inline-TVF's own column metadata at CREATE/ALTER time
/// and never refreshes it when an upstream base column is later retyped).
///
/// Read-only: the DMV parses, binds and compiles the supplied batch text and returns result-set
/// metadata WITHOUT executing it - no rows from any user table, same compile-only principle as
/// the existing SET SHOWPLAN_XML probes CLAUDE.md's Verify oracle already relies on. Every probe
/// text this reader builds is itself asserted SELECT-only by <see cref="LiveReadOnlyGuard"/>
/// before being bound as a parameter, on top of the outer query going through
/// <see cref="LiveReadOnlyGuard.CreateReadOnlyCommand"/> like every other live query.
///
/// Views are described in ONE round trip via <c>CROSS APPLY</c> over <c>sys.objects</c> - the
/// server builds each view's own probe text with <c>QUOTENAME</c>, so this never sends per-view
/// SQL text at all. Inline TVFs need a dummy, type-matched argument list synthesized from
/// <c>sys.parameters</c> (<see cref="LiveDescribeProbeBuilder"/>), so those are described one
/// object at a time - a much smaller population in practice than views, and each needs its own
/// probe text anyway.
/// </summary>
public static class LiveDescribedColumnReader
{
    /// <summary>
    /// Batch-describes every non-system view via one <c>CROSS APPLY</c> round trip, keyed by
    /// <c>schema.object</c>. An object whose probe failed to compile (dropped column, etc.)
    /// comes back as a row with <c>error_number</c>/<c>error_message</c> set and no column data -
    /// the DMV reports this in-band per object rather than aborting the whole batch, so one
    /// broken view never suppresses another view's result.
    /// </summary>
    public static async Task<Dictionary<string, DescribedObject>> DescribeViewsAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.name AS schema_name, o.name AS object_name,
                   r.error_number, r.error_message,
                   r.name AS column_name, ty.name AS type_name, r.max_length, r.precision, r.scale, r.collation_name
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            CROSS APPLY sys.dm_exec_describe_first_result_set(
                N'SELECT * FROM ' + QUOTENAME(s.name) + N'.' + QUOTENAME(o.name), NULL, 0) r
            LEFT JOIN sys.types ty ON ty.user_type_id = r.system_type_id
            WHERE o.type = 'V' AND o.is_ms_shipped = 0
            ORDER BY o.object_id, r.column_ordinal;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadDescribedObjectsAsync(reader, qualifiedNameColumns: (0, 1), cancellationToken);
    }

    /// <summary>
    /// Describes one inline TVF against a probe text this reader builds and binds as a
    /// parameter - never concatenated into the command text - and re-asserts read-only on that
    /// probe text before sending it, belt-and-braces on top of the outer command already going
    /// through <see cref="LiveReadOnlyGuard"/>.
    /// </summary>
    public static async Task<DescribedObject> DescribeFunctionAsync(
        SqlConnection connection, string probeText, CancellationToken cancellationToken)
    {
        LiveReadOnlyGuard.AssertSelectOnly(probeText);

        const string sql = """
            SELECT r.error_number, r.error_message,
                   r.name AS column_name, ty.name AS type_name, r.max_length, r.precision, r.scale, r.collation_name
            FROM sys.dm_exec_describe_first_result_set(@probeText, NULL, 0) r
            LEFT JOIN sys.types ty ON ty.user_type_id = r.system_type_id
            ORDER BY r.column_ordinal;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);
        command.Parameters.AddWithValue("@probeText", probeText);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var errorNumber = -1;
        var errorMessage = "";
        var columns = new Dictionary<string, LiveLineageParityChecker.ActualColumn>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!await reader.IsDBNullAsync(0, cancellationToken))
            {
                errorNumber = reader.GetInt32(0);
                errorMessage = await reader.IsDBNullAsync(1, cancellationToken) ? "" : reader.GetString(1);
                continue;
            }

            AddColumnRow(reader, offset: 2, columns);
        }

        return errorNumber >= 0 ? DescribedObject.FromError(errorNumber, errorMessage) : DescribedObject.FromColumns(columns);
    }

    /// <summary>
    /// Reads every inline TVF's own parameter list in ONE round trip, resolved through
    /// <c>system_type_id</c> rather than just <c>user_type_id</c> so a parameter declared with a
    /// user-defined/alias scalar type (<c>CREATE TYPE dbo.MyId FROM int</c>) still resolves to
    /// its underlying base type - joining only <c>user_type_id</c>, as the corpus oracle's own
    /// <c>FunctionParameterReader</c> does, would leave <see cref="LiveTypeMapper.Map"/> unable
    /// to recognise the alias name at all and make the whole function unprobeable for no reason.
    /// </summary>
    public static async Task<Dictionary<string, List<FunctionParameterSpec>>> ReadFunctionParametersAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.name AS schema_name, o.name AS object_name, p.name AS parameter_name,
                   ty.name AS type_name, p.max_length, p.precision, p.scale, ut.is_table_type
            FROM sys.parameters p
            JOIN sys.objects o ON o.object_id = p.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.types ut ON ut.user_type_id = p.user_type_id
            LEFT JOIN sys.types ty ON ty.user_type_id = ut.system_type_id
            WHERE o.is_ms_shipped = 0 AND o.type = 'IF' AND p.parameter_id > 0
            ORDER BY o.object_id, p.parameter_id;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var byObject = new Dictionary<string, List<FunctionParameterSpec>>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            var qualifiedName = $"{reader.GetString(0)}.{reader.GetString(1)}";
            var parameterName = reader.GetString(2);
            var isTableType = reader.GetBoolean(7);
            var type = isTableType
                ? null
                : LiveTypeMapper.BuildType(
                    reader.GetString(3), reader.GetInt16(4), reader.GetByte(5), reader.GetByte(6), collationName: null);

            if (!byObject.TryGetValue(qualifiedName, out var parameters))
            {
                parameters = [];
                byObject[qualifiedName] = parameters;
            }

            parameters.Add(new FunctionParameterSpec(parameterName, type, isTableType));
        }

        return byObject;
    }

    private static async Task<Dictionary<string, DescribedObject>> ReadDescribedObjectsAsync(
        SqlDataReader reader, (int Schema, int Object) qualifiedNameColumns, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, (int Number, string Message)>(StringComparer.OrdinalIgnoreCase);
        var columnsByObject = new Dictionary<string, Dictionary<string, LiveLineageParityChecker.ActualColumn>>(StringComparer.OrdinalIgnoreCase);

        while (await reader.ReadAsync(cancellationToken))
        {
            var qualifiedName = $"{reader.GetString(qualifiedNameColumns.Schema)}.{reader.GetString(qualifiedNameColumns.Object)}";

            if (!await reader.IsDBNullAsync(2, cancellationToken))
            {
                errors[qualifiedName] = (reader.GetInt32(2), await reader.IsDBNullAsync(3, cancellationToken) ? "" : reader.GetString(3));
                continue;
            }

            if (!columnsByObject.TryGetValue(qualifiedName, out var columns))
            {
                columns = new Dictionary<string, LiveLineageParityChecker.ActualColumn>(StringComparer.OrdinalIgnoreCase);
                columnsByObject[qualifiedName] = columns;
            }

            AddColumnRow(reader, offset: 4, columns);
        }

        var result = new Dictionary<string, DescribedObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var (qualifiedName, columns) in columnsByObject)
        {
            if (!errors.ContainsKey(qualifiedName))
            {
                result[qualifiedName] = DescribedObject.FromColumns(columns);
            }
        }

        foreach (var (qualifiedName, error) in errors)
        {
            result[qualifiedName] = DescribedObject.FromError(error.Number, error.Message);
        }

        return result;
    }

    private static void AddColumnRow(
        SqlDataReader reader, int offset, Dictionary<string, LiveLineageParityChecker.ActualColumn> columns)
    {
        // A described row with no column name (a scalar-less result set, or a row this DMV
        // returned for reasons unrelated to a real projected column) contributes nothing to
        // compare against - skipped rather than crashing on a null column-name key.
        if (reader.IsDBNull(offset) || reader.IsDBNull(offset + 1))
        {
            return;
        }

        columns[reader.GetString(offset)] = new LiveLineageParityChecker.ActualColumn(
            TypeName: reader.GetString(offset + 1),
            MaxLength: reader.GetInt16(offset + 2),
            Precision: reader.GetByte(offset + 3),
            Scale: reader.GetByte(offset + 4),
            CollationName: reader.IsDBNull(offset + 5) ? null : reader.GetString(offset + 5));
    }
}

/// <summary>One described view/inline-TVF's outcome: either its live column shape, or the compile error the engine returned instead.</summary>
public sealed class DescribedObject
{
    private DescribedObject(Dictionary<string, LiveLineageParityChecker.ActualColumn>? columns, int errorNumber, string? errorMessage)
    {
        Columns = columns;
        ErrorNumber = errorNumber;
        ErrorMessage = errorMessage;
    }

    public Dictionary<string, LiveLineageParityChecker.ActualColumn>? Columns { get; }

    public int ErrorNumber { get; }

    public string? ErrorMessage { get; }

    public bool IsError => Columns is null;

    public static DescribedObject FromColumns(Dictionary<string, LiveLineageParityChecker.ActualColumn> columns) => new(columns, 0, null);

    public static DescribedObject FromError(int errorNumber, string errorMessage) => new(null, errorNumber, errorMessage);
}
