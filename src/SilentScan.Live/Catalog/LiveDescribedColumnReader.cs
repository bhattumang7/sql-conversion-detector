using Microsoft.Data.SqlClient;
using SilentScan.Verify.Catalog;

namespace SilentScan.Live.Catalog;

public static class LiveDescribedColumnReader
{
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

public static Task<DescribedObject> DescribeFunctionAsync(
        SqlConnection connection, string probeText, CancellationToken cancellationToken)
    {
        LiveReadOnlyGuard.AssertSelectOnly(probeText);
        return DescribeAsync(connection, probeText, cancellationToken);
    }

public static async Task<DescribedResultSet> DescribeProcedureOrderedAsync(
        SqlConnection connection, string probeText, CancellationToken cancellationToken)
    {
        LiveReadOnlyGuard.AssertDescribeFirstResultSetProbeOnly(probeText);

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
        var columns = new List<DescribedResultColumn>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!await reader.IsDBNullAsync(0, cancellationToken))
            {
                errorNumber = reader.GetInt32(0);
                errorMessage = await reader.IsDBNullAsync(1, cancellationToken) ? "" : reader.GetString(1);
                continue;
            }

            var name = await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2);
            columns.Add(new DescribedResultColumn(
                name,
                new LiveLineageParityChecker.ActualColumn(
                    TypeName: reader.GetString(3),
                    MaxLength: reader.GetInt16(4),
                    Precision: reader.GetByte(5),
                    Scale: reader.GetByte(6),
                    CollationName: await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetString(7))));
        }

        return errorNumber >= 0 ? new DescribedResultSet(errorNumber, errorMessage, null) : new DescribedResultSet(0, null, columns);
    }

    private static async Task<DescribedObject> DescribeAsync(
        SqlConnection connection, string probeText, CancellationToken cancellationToken)
    {
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

public static async Task<Dictionary<string, List<ProcedureParameterSpec>>> ReadProcedureParametersAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.name AS schema_name, o.name AS object_name, p.name AS parameter_name,
                   ut.is_table_type, p.is_output
            FROM sys.parameters p
            JOIN sys.objects o ON o.object_id = p.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.types ut ON ut.user_type_id = p.user_type_id
            WHERE o.is_ms_shipped = 0 AND o.type = 'P' AND p.parameter_id > 0
            ORDER BY o.object_id, p.parameter_id;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var byObject = new Dictionary<string, List<ProcedureParameterSpec>>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            var qualifiedName = $"{reader.GetString(0)}.{reader.GetString(1)}";
            var parameterName = reader.GetString(2);
            var isTableType = reader.GetBoolean(3);
            var isOutput = reader.GetBoolean(4);

            if (!byObject.TryGetValue(qualifiedName, out var parameters))
            {
                parameters = [];
                byObject[qualifiedName] = parameters;
            }

            parameters.Add(new ProcedureParameterSpec(parameterName, isTableType, isOutput));
        }

        return byObject;
    }

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

public sealed record DescribedResultColumn(string? Name, LiveLineageParityChecker.ActualColumn Column);

public sealed record DescribedResultSet(int ErrorNumber, string? ErrorMessage, IReadOnlyList<DescribedResultColumn>? Columns)
{
    public bool IsError => Columns is null;
}
