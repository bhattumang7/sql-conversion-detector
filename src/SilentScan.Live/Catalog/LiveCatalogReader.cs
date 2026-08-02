using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;

namespace SilentScan.Live.Catalog;

/// <summary>
/// Builds a <see cref="DatabaseCatalog"/> directly from a live database's own system metadata
/// (<c>sys.tables</c>/<c>sys.columns</c>/<c>sys.types</c>/<c>sys.indexes</c>) instead of
/// inferring it from parsed DDL text the way <c>SilentScan.Core.Catalog.CatalogBuilder</c> does
/// for file-mode scans. Types, per-column collations, and index shape all come straight from
/// the engine, so this is strictly more precise than DDL inference: there is no COLLATE clause
/// to have been omitted or a database-default collation to guess at, and a computed column's
/// type is whatever the engine itself already resolved it to (<c>sys.columns</c> reports a real
/// type for a computed column exactly like an ordinary one - unlike file mode, no expression
/// re-derivation is needed here at all).
/// Issues metadata <c>SELECT</c>s only - never any DDL or DML against the connected database.
/// </summary>
public sealed class LiveCatalogReader
{
    private readonly string _connectionString;

    public LiveCatalogReader(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<DatabaseCatalog> ReadAsync(CancellationToken cancellationToken = default)
    {
        var catalog = new DatabaseCatalog();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        catalog.DefaultCollation = await ReadDatabaseDefaultCollationAsync(connection, cancellationToken);

        foreach (var (qualifiedName, underlyingType) in await ReadTypeAliasesAsync(connection, cancellationToken))
        {
            catalog.AddTypeAlias(qualifiedName, underlyingType);
        }

        var tables = await ReadTablesAsync(connection, cancellationToken);
        var columnsByTable = await ReadColumnsAsync(connection, catalog.Skipped, cancellationToken);
        var indexesByTable = await ReadIndexesAsync(connection, cancellationToken);

        foreach (var (objectId, schemaName, tableName) in tables)
        {
            var qualifiedName = $"{schemaName}.{tableName}";
            catalog.AddOrReplace(new CatalogTable(
                SchemaName: schemaName,
                Name: tableName,
                Kind: CatalogTableKind.Table,
                Columns: columnsByTable.GetValueOrDefault(objectId, []),
                Indexes: indexesByTable.GetValueOrDefault(objectId, []),
                SourcePath: qualifiedName,
                SourceLine: 0));
        }

        return catalog;
    }

    private static async Task<Collation?> ReadDatabaseDefaultCollationAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateReadOnlyCommand("SELECT collation_name FROM sys.databases WHERE database_id = DB_ID();");
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string name ? new Collation(name, CollationSource.DatabaseDefaultFromDdl) : null;
    }

    private static async Task<List<(string QualifiedName, SqlType UnderlyingType)>> ReadTypeAliasesAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.name AS schema_name, t.name AS alias_name, bt.name AS base_type_name,
                   t.max_length, t.precision, t.scale, t.collation_name
            FROM sys.types t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.types bt ON bt.system_type_id = t.system_type_id AND bt.user_type_id = bt.system_type_id
            WHERE t.is_user_defined = 1 AND t.is_table_type = 0;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var aliases = new List<(string, SqlType)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var baseTypeName = reader.GetString(2);
            var maxLength = reader.GetInt16(3);
            var precision = reader.GetByte(4);
            var scale = reader.GetByte(5);
            var collationName = await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetString(6);

            var underlyingType = LiveTypeMapper.BuildType(baseTypeName, maxLength, precision, scale, collationName);
            if (underlyingType is null)
            {
                continue;
            }

            aliases.Add(($"{reader.GetString(0)}.{reader.GetString(1)}", underlyingType));
        }

        return aliases;
    }

    private static async Task<List<(int ObjectId, string SchemaName, string TableName)>> ReadTablesAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT t.object_id, s.name AS schema_name, t.name AS table_name
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var tables = new List<(int, string, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }

        return tables;
    }

    private static async Task<Dictionary<int, List<CatalogColumn>>> ReadColumnsAsync(
        SqlConnection connection, SkipLedger skipLedger, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT c.object_id, s.name AS schema_name, t.name AS table_name, c.name AS column_name,
                   ty.name AS type_name, c.max_length, c.precision, c.scale, c.collation_name,
                   c.is_nullable, c.is_identity, c.is_computed, cc.is_persisted
            FROM sys.columns c
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            WHERE t.is_ms_shipped = 0
            ORDER BY c.object_id, c.column_id;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var columnsByTable = new Dictionary<int, List<CatalogColumn>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var objectId = reader.GetInt32(0);
            var schemaName = reader.GetString(1);
            var tableName = reader.GetString(2);
            var columnName = reader.GetString(3);
            var typeName = reader.GetString(4);
            var maxLength = reader.GetInt16(5);
            var precision = reader.GetByte(6);
            var scale = reader.GetByte(7);
            var collationName = await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetString(8);
            var isNullable = reader.GetBoolean(9);
            var isIdentity = reader.GetBoolean(10);
            var isComputed = reader.GetBoolean(11);
            var isPersisted = !await reader.IsDBNullAsync(12, cancellationToken) && reader.GetBoolean(12);

            var type = LiveTypeMapper.BuildType(typeName, maxLength, precision, scale, collationName);
            if (type is null)
            {
                // Never silently drop the column - callers must see Type=null and treat it as
                // UNKNOWN, matching how an unresolvable DDL-mode column type is still recorded
                // (CatalogColumn's own doc comment) rather than omitted entirely.
                skipLedger.Record(
                    AnalysisPass.Catalog, $"{schemaName}.{tableName}", 0, 0,
                    "live column type",
                    $"'{columnName}' has sys.types name '{typeName}', which this pass does not map to a scalar comparison type (CLR UDT, geography/geometry, hierarchyid, or similar) - type left UNKNOWN.");
            }

            if (!columnsByTable.TryGetValue(objectId, out var columns))
            {
                columns = [];
                columnsByTable[objectId] = columns;
            }

            columns.Add(new CatalogColumn(columnName, type, isNullable, isIdentity, isComputed, isPersisted));
        }

        return columnsByTable;
    }

    private static async Task<Dictionary<int, List<CatalogIndex>>> ReadIndexesAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT i.object_id, i.index_id, i.name AS index_name, i.type_desc, i.is_unique,
                   i.is_primary_key, i.is_unique_constraint, i.has_filter, i.is_disabled,
                   ic.key_ordinal, ic.is_included_column, ic.index_column_id, c.name AS column_name
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE t.is_ms_shipped = 0 AND i.type_desc <> 'HEAP'
            ORDER BY i.object_id, i.index_id, ic.index_column_id;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var rowsByIndex = new Dictionary<(int ObjectId, int IndexId), IndexRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var objectId = reader.GetInt32(0);
            var indexId = reader.GetInt32(1);
            var key = (objectId, indexId);

            if (!rowsByIndex.TryGetValue(key, out var row))
            {
                var indexName = await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2);
                row = new IndexRow(
                    Name: indexName,
                    TypeDesc: reader.GetString(3),
                    IsUnique: reader.GetBoolean(4),
                    IsPrimaryKey: reader.GetBoolean(5),
                    IsUniqueConstraint: reader.GetBoolean(6),
                    HasFilter: reader.GetBoolean(7),
                    IsDisabled: reader.GetBoolean(8),
                    KeyColumns: [],
                    IncludedColumns: []);
                rowsByIndex[key] = row;
            }

            var isIncluded = reader.GetBoolean(10);
            var columnName = reader.GetString(12);
            if (isIncluded)
            {
                row.IncludedColumns.Add(columnName);
            }
            else
            {
                row.KeyColumns.Add((reader.GetByte(9), columnName));
            }
        }

        var indexesByTable = new Dictionary<int, List<CatalogIndex>>();
        foreach (var ((objectId, _), row) in rowsByIndex)
        {
            var kind = ClassifyIndexKind(row);

            var orderedKeyColumns = row.KeyColumns.OrderBy(k => k.Ordinal).Select(k => k.Name).ToList();

            var index = new CatalogIndex(
                Name: row.Name,
                Kind: kind,
                IsUnique: row.IsUnique,
                KeyColumns: orderedKeyColumns,
                IncludedColumns: row.IncludedColumns,
                IsFiltered: row.HasFilter,
                IsColumnstore: row.TypeDesc.Contains("COLUMNSTORE", StringComparison.OrdinalIgnoreCase),
                IsDisabled: row.IsDisabled);

            if (!indexesByTable.TryGetValue(objectId, out var indexes))
            {
                indexes = [];
                indexesByTable[objectId] = indexes;
            }

            indexes.Add(index);
        }

        return indexesByTable;
    }

    private static CatalogIndexKind ClassifyIndexKind(IndexRow row)
    {
        if (row.IsPrimaryKey)
        {
            return CatalogIndexKind.PrimaryKey;
        }

        return row.IsUniqueConstraint ? CatalogIndexKind.UniqueConstraint : CatalogIndexKind.Index;
    }

    private sealed record IndexRow(
        string? Name,
        string TypeDesc,
        bool IsUnique,
        bool IsPrimaryKey,
        bool IsUniqueConstraint,
        bool HasFilter,
        bool IsDisabled,
        List<(int Ordinal, string Name)> KeyColumns,
        List<string> IncludedColumns);
}
