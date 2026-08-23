using Microsoft.Data.SqlClient;

namespace SilentScan.Verify.Oracle;

public sealed class ColumnCatalogReader
{
    private readonly SqlServerOptions _options;

    public ColumnCatalogReader(SqlServerOptions options)
    {
        _options = options;
    }

public async Task<IReadOnlyList<CatalogColumnInfo>> ReadColumnsAsync(
        string database, string schemaQualifiedObjectName, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                c.name          AS column_name,
                t.name          AS type_name,
                c.max_length,
                c.precision,
                c.scale,
                c.collation_name,
                c.is_nullable
            FROM sys.columns c
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(@objectName)
            ORDER BY c.column_id;
            """;

        await using var connection = new SqlConnection(_options.BuildConnectionString(database));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@objectName", schemaQualifiedObjectName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<CatalogColumnInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var collationIsNull = await reader.IsDBNullAsync(5, cancellationToken);
            results.Add(new CatalogColumnInfo(
                ColumnName: reader.GetString(0),
                TypeName: reader.GetString(1),
                MaxLength: reader.GetInt16(2),
                Precision: reader.GetByte(3),
                Scale: reader.GetByte(4),
                CollationName: collationIsNull ? null : reader.GetString(5),
                IsNullable: reader.GetBoolean(6)));
        }

        return results;
    }
}
