using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Verify.Catalog;

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

        catalog.CurrentDatabaseName = connection.Database;
        catalog.DefaultCollation = await ReadDatabaseDefaultCollationAsync(connection, cancellationToken);
        catalog.CompatibilityLevel = await ReadCompatibilityLevelAsync(connection, cancellationToken);
        catalog.IsRecursiveTriggersEnabled = await ReadIsRecursiveTriggersEnabledAsync(connection, cancellationToken);
        catalog.IsNestedTriggersEnabled = await ReadIsNestedTriggersEnabledAsync(connection, cancellationToken);
        catalog.IsDisallowResultsFromTriggersEnabled = await ReadIsDisallowResultsFromTriggersEnabledAsync(connection, cancellationToken);
        catalog.IsAutoCreateStatsOn = await ReadIsAutoCreateStatsOnAsync(connection, cancellationToken);
        catalog.IsAnsiNullDefaultOn = await ReadIsAnsiNullDefaultOnAsync(connection, cancellationToken);
        catalog.IsReadCommittedSnapshotOn = await ReadIsReadCommittedSnapshotOnAsync(connection, cancellationToken);

        foreach (var (qualifiedName, underlyingType) in await ReadTypeAliasesAsync(connection, cancellationToken))
        {
            catalog.AddTypeAlias(qualifiedName, underlyingType);
        }

        foreach (var (qualifiedName, targetQualifiedName) in await ReadSynonymsAsync(connection, cancellationToken))
        {
            catalog.AddSynonym(qualifiedName, targetQualifiedName);
        }

        var tables = await ReadTablesAsync(connection, cancellationToken);
        var columnsByTable = await ReadColumnsAsync(connection, catalog.Skipped, cancellationToken);
        var indexesByTable = await ReadIndexesAsync(connection, cancellationToken);
        var statisticsByTable = await ReadStatisticsAsync(connection, cancellationToken);
        var filegroupByTable = await ReadTableFilegroupsAsync(connection, cancellationToken);
        var ruleConstraintTables = await ReadRuleConstraintTablesAsync(connection, cancellationToken);
        var cdcPartitionSwitchDisallowedTables = await ReadCdcPartitionSwitchDisallowedTablesAsync(connection, cancellationToken);
        var partitionSchemeByTable = await ReadTablePartitionSchemesAsync(connection, cancellationToken);
        var fullTextIndexTables = await ReadFullTextIndexTablesAsync(connection, cancellationToken);

        foreach (var (schemeName, partitionNumber, filegroupName) in await ReadPartitionFilegroupsAsync(connection, cancellationToken))
        {
            catalog.AddPartitionFilegroup(schemeName, partitionNumber, filegroupName);
        }

        foreach (var (objectId, schemaName, tableName, isMemoryOptimized) in tables)
        {
            var qualifiedName = $"{schemaName}.{tableName}";
            var (filegroupName, filegroupIsReadOnly) = filegroupByTable.GetValueOrDefault(objectId);
            catalog.AddOrReplace(new CatalogTable(
                SchemaName: schemaName,
                Name: tableName,
                Kind: CatalogTableKind.Table,
                Columns: columnsByTable.GetValueOrDefault(objectId, []),
                Indexes: indexesByTable.GetValueOrDefault(objectId, []),
                SourcePath: qualifiedName,
                SourceLine: 0,
                IsMemoryOptimized: isMemoryOptimized,
                Statistics: statisticsByTable.GetValueOrDefault(objectId, []),
                FilegroupName: filegroupName,
                FilegroupIsReadOnly: filegroupIsReadOnly,
                HasRuleConstraint: ruleConstraintTables.Contains(objectId),
                CdcPartitionSwitchDisallowed: cdcPartitionSwitchDisallowedTables.Contains(objectId),
                PartitionSchemeName: partitionSchemeByTable.GetValueOrDefault(objectId),
                HasFullTextIndex: fullTextIndexTables.Contains(objectId)));
        }

        foreach (var (schemaName, functionName, columns) in await ReadClrTableValuedFunctionShapesAsync(connection, catalog.Skipped, cancellationToken))
        {
            var qualifiedName = $"{schemaName}.{functionName}";
            catalog.AddOrReplace(new CatalogTable(
                SchemaName: schemaName,
                Name: functionName,
                Kind: CatalogTableKind.ClrTableValuedFunction,
                Columns: columns,
                Indexes: [],
                SourcePath: qualifiedName,
                SourceLine: 0));
        }

        foreach (var (qualifiedName, returnType) in await ReadClrScalarFunctionReturnTypesAsync(connection, catalog.DefaultCollation?.Name, cancellationToken))
        {
            catalog.AddScalarFunctionReturnType(qualifiedName, returnType);
        }

        foreach (var (qualifiedName, kind) in await ReadTableValuedFunctionKindsAsync(connection, cancellationToken))
        {
            catalog.AddTableValuedFunctionKind(qualifiedName, kind);
        }

        foreach (var (qualifiedName, info) in await ReadScalarUdfInfoAsync(connection, cancellationToken))
        {
            catalog.AddScalarUdfInfo(qualifiedName, info);
        }

        foreach (var reference in await ReadSchemaExpressionsAsync(connection, cancellationToken))
        {
            catalog.AddSchemaExpression(reference);
        }

        foreach (var foreignKey in await ReadForeignKeysAsync(connection, cancellationToken))
        {
            catalog.AddForeignKey(foreignKey);
        }

        foreach (var checkConstraint in await ReadCheckConstraintsAsync(connection, cancellationToken))
        {
            catalog.AddCheckConstraint(checkConstraint);
        }

        foreach (var triggerEvent in await ReadTriggerEventsAsync(connection, cancellationToken))
        {
            catalog.AddTriggerEvent(triggerEvent);
        }

        foreach (var securityPredicate in await ReadSecurityPredicatesAsync(connection, cancellationToken))
        {
            catalog.AddSecurityPredicate(securityPredicate);
        }

        foreach (var (qualifiedName, indexes) in await ReadIndexedViewsAsync(connection, cancellationToken))
        {
            catalog.AddIndexedView(qualifiedName, indexes);
        }

        foreach (var (qualifiedName, columnNames) in await ReadViewCompiledColumnsAsync(connection, cancellationToken))
        {
            catalog.AddViewCompiledColumns(qualifiedName, columnNames);
        }

        foreach (var pair in await ReadTemporalTablePairsAsync(connection, cancellationToken))
        {
            catalog.AddTemporalTablePair(pair);
        }

        return catalog;
    }

    private static async Task<List<(string QualifiedName, ScalarUdfInfo Info)>> ReadScalarUdfInfoAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const int InvalidColumnNameErrorNumber = 207;

        const string sqlWithInlineable = """
            SELECT s.name AS schema_name, o.name AS function_name, m.is_schema_bound, m.is_inlineable
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.sql_modules m ON m.object_id = o.object_id
            WHERE o.type = 'FN' AND o.is_ms_shipped = 0;
            """;

        const string sqlWithoutInlineable = """
            SELECT s.name AS schema_name, o.name AS function_name, m.is_schema_bound
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.sql_modules m ON m.object_id = o.object_id
            WHERE o.type = 'FN' AND o.is_ms_shipped = 0;
            """;

        var tSqlUdfs = new List<(string, ScalarUdfInfo)>();

        try
        {
            await using var command = connection.CreateReadOnlyCommand(sqlWithInlineable);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var qualifiedName = $"{reader.GetString(0)}.{reader.GetString(1)}";
                var isSchemaBound = reader.GetBoolean(2);
                var isInlineable = await reader.IsDBNullAsync(3, cancellationToken) ? (bool?)null : reader.GetBoolean(3);
                tSqlUdfs.Add((qualifiedName, new ScalarUdfInfo(ScalarUdfKind.TSql, isSchemaBound, isInlineable, InlineabilityBlocker: null, ClrDataAccess: null)));
            }
        }
        catch (SqlException ex) when (ex.Number == InvalidColumnNameErrorNumber)
        {
            tSqlUdfs.Clear();
            await using var command = connection.CreateReadOnlyCommand(sqlWithoutInlineable);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var qualifiedName = $"{reader.GetString(0)}.{reader.GetString(1)}";
                var isSchemaBound = reader.GetBoolean(2);
                tSqlUdfs.Add((qualifiedName, new ScalarUdfInfo(ScalarUdfKind.TSql, isSchemaBound, EngineIsInlineable: null, InlineabilityBlocker: null, ClrDataAccess: null)));
            }
        }

        var clrUdfs = await ReadClrScalarUdfInfoAsync(connection, cancellationToken);

        return [.. tSqlUdfs, .. clrUdfs];
    }

    private static async Task<List<(string QualifiedName, ScalarUdfInfo Info)>> ReadClrScalarUdfInfoAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string namesSql = """
            SELECT o.object_id, s.name AS schema_name, o.name AS function_name
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type = 'FS' AND o.is_ms_shipped = 0;
            """;

        var functions = new List<(int ObjectId, string QualifiedName)>();
        await using (var command = connection.CreateReadOnlyCommand(namesSql))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                functions.Add((reader.GetInt32(0), $"{reader.GetString(1)}.{reader.GetString(2)}"));
            }
        }

        var clrUdfs = new List<(string, ScalarUdfInfo)>();
        foreach (var (objectId, qualifiedName) in functions)
        {
            var dataAccess = await TryReadClrDataAccessAsync(connection, objectId, cancellationToken);
            clrUdfs.Add((qualifiedName, new ScalarUdfInfo(ScalarUdfKind.Clr, IsSchemaBound: null, EngineIsInlineable: null, InlineabilityBlocker: null, dataAccess)));
        }

        return clrUdfs;
    }

    private static async Task<bool?> TryReadClrDataAccessAsync(SqlConnection connection, int objectId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT OBJECTPROPERTYEX(@objectId, 'UserDataAccess') AS user_data_access,
                   OBJECTPROPERTYEX(@objectId, 'SystemDataAccess') AS system_data_access;
            """;

        try
        {
            await using var command = connection.CreateReadOnlyCommand(sql);
            command.Parameters.AddWithValue("@objectId", objectId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var userDataAccess = await reader.IsDBNullAsync(0, cancellationToken) ? (bool?)null : reader.GetInt32(0) != 0;
            var systemDataAccess = await reader.IsDBNullAsync(1, cancellationToken) ? (bool?)null : reader.GetInt32(1) != 0;

            return userDataAccess is null && systemDataAccess is null
                ? null
                : (userDataAccess ?? false) || (systemDataAccess ?? false);
        }
        catch (SqlException)
        {
            return null;
        }
    }

    private static async Task<List<(string QualifiedName, TableValuedFunctionKind Kind)>> ReadTableValuedFunctionKindsAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.name AS schema_name, o.name AS function_name, o.type
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ('IF', 'TF', 'FT') AND o.is_ms_shipped = 0;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var kinds = new List<(string, TableValuedFunctionKind)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var qualifiedName = $"{reader.GetString(0)}.{reader.GetString(1)}";
            var kind = reader.GetString(2).Trim() switch
            {
                "IF" => (TableValuedFunctionKind?)TableValuedFunctionKind.Inline,
                "TF" => TableValuedFunctionKind.MultiStatement,
                "FT" => TableValuedFunctionKind.Clr,
                _ => null,
            };

            if (kind is { } resolvedKind)
            {
                kinds.Add((qualifiedName, resolvedKind));
            }
        }

        return kinds;
    }

    private static async Task<List<SchemaExpressionReference>> ReadSchemaExpressionsAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.name AS schema_name, t.name AS table_name, c.name AS column_name,
                   'ComputedColumn' AS kind, cc.definition AS definition_text
            FROM sys.computed_columns cc
            JOIN sys.tables t ON t.object_id = cc.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = cc.object_id AND c.column_id = cc.column_id
            WHERE t.is_ms_shipped = 0
            UNION ALL
            SELECT s.name, t.name, c.name, 'DefaultConstraint', dc.definition
            FROM sys.default_constraints dc
            JOIN sys.tables t ON t.object_id = dc.parent_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
            WHERE t.is_ms_shipped = 0
            UNION ALL
            SELECT s.name, t.name, c.name, 'CheckConstraint', ck.definition
            FROM sys.check_constraints ck
            JOIN sys.tables t ON t.object_id = ck.parent_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            LEFT JOIN sys.columns c ON c.object_id = ck.parent_object_id AND c.column_id = ck.parent_column_id
            WHERE t.is_ms_shipped = 0;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var references = new List<SchemaExpressionReference>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var schemaName = reader.GetString(0);
            var tableName = reader.GetString(1);
            var columnName = await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2);
            var kind = reader.GetString(3) switch
            {
                "ComputedColumn" => SchemaDependencyKind.ComputedColumn,
                "DefaultConstraint" => SchemaDependencyKind.DefaultConstraint,
                _ => SchemaDependencyKind.CheckConstraint,
            };
            var definitionText = await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4);
            if (definitionText is null)
            {
                continue;
            }

            var qualifiedName = $"{schemaName}.{tableName}";
            references.Add(new SchemaExpressionReference(kind, qualifiedName, columnName, definitionText, qualifiedName, Line: 0));
        }

        return references;
    }

    private static async Task<List<ForeignKeyRelationship>> ReadForeignKeysAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT fk.name AS constraint_name,
                   ps.name AS parent_schema, pt.name AS parent_table, pc.name AS parent_column,
                   rs.name AS referenced_schema, rt.name AS referenced_table, rc.name AS referenced_column,
                   fk.is_not_trusted, fk.is_disabled, fk.delete_referential_action, fk.update_referential_action
            FROM sys.foreign_key_columns fkc
            JOIN sys.foreign_keys fk ON fk.object_id = fkc.constraint_object_id
            JOIN sys.tables pt ON pt.object_id = fkc.parent_object_id
            JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.tables rt ON rt.object_id = fkc.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE pt.is_ms_shipped = 0 AND rt.is_ms_shipped = 0;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var relationships = new List<ForeignKeyRelationship>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            relationships.Add(new ForeignKeyRelationship(
                ConstraintName: reader.GetString(0),
                ParentTableQualifiedName: $"{reader.GetString(1)}.{reader.GetString(2)}",
                ParentColumnName: reader.GetString(3),
                ReferencedTableQualifiedName: $"{reader.GetString(4)}.{reader.GetString(5)}",
                ReferencedColumnName: reader.GetString(6),
                IsNotTrusted: reader.GetBoolean(7),
                IsDisabled: reader.GetBoolean(8),
                DeleteAction: (ReferentialAction)reader.GetByte(9),
                UpdateAction: (ReferentialAction)reader.GetByte(10)));
        }

        return relationships;
    }

    private static async Task<List<CatalogCheckConstraint>> ReadCheckConstraintsAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT cc.name AS constraint_name, s.name AS schema_name, t.name AS table_name,
                   cc.is_not_trusted, cc.is_disabled, cc.definition
            FROM sys.check_constraints cc
            JOIN sys.tables t ON t.object_id = cc.parent_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE t.is_ms_shipped = 0;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var constraints = new List<CatalogCheckConstraint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            constraints.Add(new CatalogCheckConstraint(
                ConstraintName: reader.GetString(0),
                TableQualifiedName: $"{reader.GetString(1)}.{reader.GetString(2)}",
                IsNotTrusted: reader.GetBoolean(3),
                IsDisabled: reader.GetBoolean(4),
                DefinitionText: await reader.IsDBNullAsync(5, cancellationToken) ? string.Empty : reader.GetString(5)));
        }

        return constraints;
    }

    private static async Task<List<CatalogTriggerEvent>> ReadTriggerEventsAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.name AS schema_name, t.name AS table_name, tr.name AS trigger_name,
                   te.type_desc, tr.is_instead_of_trigger, tr.is_disabled, te.is_first, te.is_last
            FROM sys.triggers tr
            JOIN sys.trigger_events te ON te.object_id = tr.object_id
            JOIN sys.tables t ON t.object_id = tr.parent_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE tr.parent_class = 1 AND t.is_ms_shipped = 0;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var triggerEvents = new List<CatalogTriggerEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var tableQualifiedName = $"{reader.GetString(0)}.{reader.GetString(1)}";
            var triggerQualifiedName = $"{reader.GetString(0)}.{reader.GetString(2)}";
            triggerEvents.Add(new CatalogTriggerEvent(
                TriggerQualifiedName: triggerQualifiedName,
                TableQualifiedName: tableQualifiedName,
                EventTypeDescription: reader.GetString(3),
                IsInsteadOf: reader.GetBoolean(4),
                IsDisabled: reader.GetBoolean(5),
                IsFirst: reader.GetBoolean(6),
                IsLast: reader.GetBoolean(7),
                SourcePath: triggerQualifiedName,
                SourceLine: 0));
        }

        return triggerEvents;
    }

    private static async Task<List<CatalogSecurityPredicate>> ReadSecurityPredicatesAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ps.name AS policy_schema, pol.name AS policy_name,
                   ts.name AS table_schema, tt.name AS table_name,
                   sp.predicate_definition, sp.predicate_type_desc, pol.is_enabled
            FROM sys.security_predicates sp
            JOIN sys.security_policies pol ON pol.object_id = sp.object_id
            JOIN sys.schemas ps ON ps.schema_id = pol.schema_id
            JOIN sys.tables tt ON tt.object_id = sp.target_object_id
            JOIN sys.schemas ts ON ts.schema_id = tt.schema_id
            WHERE tt.is_ms_shipped = 0;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var predicates = new List<CatalogSecurityPredicate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            predicates.Add(new CatalogSecurityPredicate(
                PolicyQualifiedName: $"{reader.GetString(0)}.{reader.GetString(1)}",
                TargetTableQualifiedName: $"{reader.GetString(2)}.{reader.GetString(3)}",
                PredicateDefinitionText: await reader.IsDBNullAsync(4, cancellationToken) ? string.Empty : reader.GetString(4),
                IsFilterPredicate: string.Equals(reader.GetString(5), "FILTER", StringComparison.OrdinalIgnoreCase),
                IsPolicyEnabled: reader.GetBoolean(6)));
        }

        return predicates;
    }

    private static async Task<List<TemporalTablePair>> ReadTemporalTablePairsAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ps.name AS current_schema, pt.name AS current_table,
                   hs.name AS history_schema, ht.name AS history_table
            FROM sys.tables pt
            JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
            JOIN sys.tables ht ON ht.object_id = pt.history_table_id
            JOIN sys.schemas hs ON hs.schema_id = ht.schema_id
            WHERE pt.temporal_type = 2 AND pt.is_ms_shipped = 0;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var pairs = new List<TemporalTablePair>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            pairs.Add(new TemporalTablePair(
                CurrentTableQualifiedName: $"{reader.GetString(0)}.{reader.GetString(1)}",
                HistoryTableQualifiedName: $"{reader.GetString(2)}.{reader.GetString(3)}"));
        }

        return pairs;
    }

    private static async Task<List<(string SchemaName, string FunctionName, List<CatalogColumn> Columns)>> ReadClrTableValuedFunctionShapesAsync(
        SqlConnection connection, SkipLedger skipLedger, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT o.object_id, s.name AS schema_name, o.name AS function_name, c.name AS column_name,
                   ty.name AS type_name, c.max_length, c.precision, c.scale, c.collation_name, c.is_nullable
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.columns c ON c.object_id = o.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE o.type = 'FT' AND o.is_ms_shipped = 0
            ORDER BY o.object_id, c.column_id;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var byFunction = new Dictionary<int, (string SchemaName, string FunctionName, List<CatalogColumn> Columns)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var objectId = reader.GetInt32(0);
            var schemaName = reader.GetString(1);
            var functionName = reader.GetString(2);
            var columnName = reader.GetString(3);
            var typeName = reader.GetString(4);
            var maxLength = reader.GetInt16(5);
            var precision = reader.GetByte(6);
            var scale = reader.GetByte(7);
            var collationName = await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetString(8);
            var isNullable = reader.GetBoolean(9);

            var type = LiveTypeMapper.BuildType(typeName, maxLength, precision, scale, collationName);
            if (type is null)
            {
                skipLedger.Record(
                    AnalysisPass.Catalog, $"{schemaName}.{functionName}", 0, 0,
                    "live column type",
                    $"'{columnName}' has sys.types name '{typeName}', which this pass does not map to a scalar comparison type (CLR UDT, geography/geometry, hierarchyid, or similar) - type left UNKNOWN.");
            }

            if (!byFunction.TryGetValue(objectId, out var entry))
            {
                entry = (schemaName, functionName, []);
                byFunction[objectId] = entry;
            }

            entry.Columns.Add(new CatalogColumn(columnName, type, isNullable, IsIdentity: false, IsComputed: false, IsPersisted: false));
        }

        return [.. byFunction.Values];
    }

    private static async Task<List<(string QualifiedName, SqlType? ReturnType)>> ReadClrScalarFunctionReturnTypesAsync(
        SqlConnection connection, string? databaseDefaultCollationName, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.name AS schema_name, o.name AS function_name,
                   ty.name AS type_name, p.max_length, p.precision, p.scale
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.parameters p ON p.object_id = o.object_id AND p.parameter_id = 0
            JOIN sys.types ty ON ty.user_type_id = p.user_type_id
            WHERE o.type = 'FS' AND o.is_ms_shipped = 0;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var returnTypes = new List<(string, SqlType?)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var qualifiedName = $"{reader.GetString(0)}.{reader.GetString(1)}";
            var typeName = reader.GetString(2);
            var maxLength = reader.GetInt16(3);
            var precision = reader.GetByte(4);
            var scale = reader.GetByte(5);

            var returnType = LiveTypeMapper.BuildType(typeName, maxLength, precision, scale, databaseDefaultCollationName);
            returnTypes.Add((qualifiedName, returnType));
        }

        return returnTypes;
    }

    private static async Task<Collation?> ReadDatabaseDefaultCollationAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateReadOnlyCommand("SELECT collation_name FROM sys.databases WHERE database_id = DB_ID();");
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string name ? new Collation(name, CollationSource.DatabaseDefaultFromDdl) : null;
    }

    private static async Task<int?> ReadCompatibilityLevelAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateReadOnlyCommand("SELECT compatibility_level FROM sys.databases WHERE database_id = DB_ID();");
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is byte level ? level : null;
    }

    private static async Task<bool?> ReadIsRecursiveTriggersEnabledAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateReadOnlyCommand("SELECT is_recursive_triggers_on FROM sys.databases WHERE database_id = DB_ID();");
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is bool isOn ? isOn : null;
    }

    private static async Task<bool?> ReadIsAutoCreateStatsOnAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateReadOnlyCommand("SELECT is_auto_create_stats_on FROM sys.databases WHERE database_id = DB_ID();");
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is bool isOn ? isOn : null;
    }

    private static async Task<bool?> ReadIsAnsiNullDefaultOnAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateReadOnlyCommand("SELECT is_ansi_null_default_on FROM sys.databases WHERE database_id = DB_ID();");
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is bool isOn ? isOn : null;
    }

    private static async Task<bool?> ReadIsReadCommittedSnapshotOnAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateReadOnlyCommand("SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID();");
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is bool isOn ? isOn : null;
    }

    private static async Task<bool?> ReadIsNestedTriggersEnabledAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateReadOnlyCommand(
            "SELECT value_in_use FROM sys.configurations WHERE name = 'nested triggers';");
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result switch
        {
            int intValue => intValue != 0,
            bool boolValue => boolValue,
            _ => null,
        };
    }

    private static async Task<bool?> ReadIsDisallowResultsFromTriggersEnabledAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateReadOnlyCommand(
            "SELECT value_in_use FROM sys.configurations WHERE name = 'disallow results from triggers';");
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result switch
        {
            int intValue => intValue != 0,
            bool boolValue => boolValue,
            _ => null,
        };
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

    private static async Task<List<(string QualifiedName, string TargetQualifiedName)>> ReadSynonymsAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.name AS schema_name, syn.name AS synonym_name, syn.base_object_name
            FROM sys.synonyms syn
            JOIN sys.schemas s ON s.schema_id = syn.schema_id;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var synonyms = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var qualifiedName = $"{reader.GetString(0)}.{reader.GetString(1)}";
            var baseObjectName = reader.GetString(2);

            if (TryParseSchemaObjectName(baseObjectName) is { } targetQualifiedName)
            {
                synonyms.Add((qualifiedName, targetQualifiedName));
            }
        }

        return synonyms;
    }

    private static string? TryParseSchemaObjectName(string rawName)
    {
        var result = SqlScriptParser.ParseText("synonym-target", $"SELECT * FROM {rawName};");
        if (result.HasErrors || result.Fragment is not TSqlScript { Batches: [{ Statements: [SelectStatement { QueryExpression: QuerySpecification { FromClause.TableReferences: [NamedTableReference namedTable] } }] }] })
        {
            return null;
        }

        return SchemaObjectNameHelper.Qualify(namedTable.SchemaObject);
    }

    private static async Task<List<(int ObjectId, string SchemaName, string TableName, bool IsMemoryOptimized)>> ReadTablesAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT t.object_id, s.name AS schema_name, t.name AS table_name, t.is_memory_optimized
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var tables = new List<(int, string, string, bool)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
        }

        return tables;
    }

    private static async Task<Dictionary<int, List<CatalogColumn>>> ReadColumnsAsync(
        SqlConnection connection, SkipLedger skipLedger, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT c.object_id, s.name AS schema_name, t.name AS table_name, c.name AS column_name,
                   ty.name AS type_name, c.max_length, c.precision, c.scale, c.collation_name,
                   c.is_nullable, c.is_identity, c.is_computed, cc.is_persisted, c.is_ansi_padded,
                   CONVERT(decimal(38,0), idc.seed_value), CONVERT(decimal(38,0), idc.increment_value),
                   CONVERT(decimal(38,0), idc.last_value), c.encryption_type,
                   c.is_masked, mc.masking_function
            FROM sys.columns c
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN sys.identity_columns idc ON idc.object_id = c.object_id AND idc.column_id = c.column_id
            LEFT JOIN sys.masked_columns mc ON mc.object_id = c.object_id AND mc.column_id = c.column_id
            WHERE t.is_ms_shipped = 0
            ORDER BY c.object_id, c.column_id;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var columnsByTable = new Dictionary<int, List<CatalogColumn>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var (objectId, column) = await ReadColumnAsync(reader, skipLedger, cancellationToken);

            if (!columnsByTable.TryGetValue(objectId, out var columns))
            {
                columns = [];
                columnsByTable[objectId] = columns;
            }

            columns.Add(column);
        }

        return columnsByTable;
    }

    private static async Task<(int ObjectId, CatalogColumn Column)> ReadColumnAsync(
        SqlDataReader reader, SkipLedger skipLedger, CancellationToken cancellationToken)
    {
        var objectId = reader.GetInt32(0);
        var schemaName = reader.GetString(1);
        var tableName = reader.GetString(2);
        var columnName = reader.GetString(3);
        var typeName = reader.GetString(4);
        var type = LiveTypeMapper.BuildType(typeName, reader.GetInt16(5), reader.GetByte(6), reader.GetByte(7), await ReadNullableStringAsync(reader, 8, cancellationToken));
        if (type is null)
        {
            skipLedger.Record(
                AnalysisPass.Catalog, $"{schemaName}.{tableName}", 0, 0,
                "live column type",
                $"'{columnName}' has sys.types name '{typeName}', which this pass does not map to a scalar comparison type (CLR UDT, geography/geometry, hierarchyid, or similar) - type left UNKNOWN.");
        }

        var encryptionType = await ReadEncryptionTypeAsync(reader, cancellationToken);
        var isMasked = reader.GetBoolean(18);
        var maskingFunctionName = MaskingFunctionNameNormalizer.Normalize(await ReadNullableStringAsync(reader, 19, cancellationToken));
        var column = new CatalogColumn(
            columnName, type, reader.GetBoolean(9), reader.GetBoolean(10), reader.GetBoolean(11),
            !await reader.IsDBNullAsync(12, cancellationToken) && reader.GetBoolean(12), reader.GetBoolean(13),
            await ReadNullableDecimalAsync(reader, 14, cancellationToken),
            await ReadNullableDecimalAsync(reader, 15, cancellationToken),
            await ReadNullableDecimalAsync(reader, 16, cancellationToken), encryptionType,
            EnclaveSupport: ColumnEncryptionEnclaveSupport.Unknown,
            IsMasked: isMasked,
            MaskingFunctionName: maskingFunctionName);
        return (objectId, column);
    }

    private static async Task<string?> ReadNullableStringAsync(SqlDataReader reader, int ordinal, CancellationToken cancellationToken) =>
        await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : reader.GetString(ordinal);

    private static async Task<decimal?> ReadNullableDecimalAsync(SqlDataReader reader, int ordinal, CancellationToken cancellationToken) =>
        await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : reader.GetDecimal(ordinal);

    private static async Task<SilentScan.Core.Catalog.ColumnEncryptionType> ReadEncryptionTypeAsync(
        SqlDataReader reader, CancellationToken cancellationToken)
    {
        if (await reader.IsDBNullAsync(17, cancellationToken))
        {
            return SilentScan.Core.Catalog.ColumnEncryptionType.None;
        }

        return reader.GetInt32(17) switch
        {
            1 => SilentScan.Core.Catalog.ColumnEncryptionType.Deterministic,
            2 => SilentScan.Core.Catalog.ColumnEncryptionType.Randomized,
            _ => SilentScan.Core.Catalog.ColumnEncryptionType.None,
        };
    }

    private static async Task<Dictionary<int, List<CatalogIndex>>> ReadIndexesAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {

        const string sql = """
            SELECT i.object_id, i.index_id, i.name AS index_name, i.type_desc, i.is_unique,
                   i.is_primary_key, i.is_unique_constraint, i.has_filter, i.is_disabled,
                   i.is_hypothetical, i.filter_definition, i.optimize_for_sequential_key,
                   ic.key_ordinal, ic.is_included_column, ic.index_column_id, c.name AS column_name,
                   ic.is_descending_key,
                   CASE WHEN ds.type = 'PS' THEN ds.name ELSE NULL END AS partition_scheme_name,
                   pc.name AS partitioning_column_name, i.ignore_dup_key,
                   i.allow_row_locks, i.allow_page_locks
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            LEFT JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            LEFT JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            LEFT JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
            LEFT JOIN sys.index_columns pic ON pic.object_id = i.object_id AND pic.index_id = i.index_id AND pic.partition_ordinal = 1
            LEFT JOIN sys.columns pc ON pc.object_id = pic.object_id AND pc.column_id = pic.column_id
            WHERE t.is_ms_shipped = 0 AND i.type_desc <> 'HEAP'
            ORDER BY i.object_id, i.index_id, ic.index_column_id;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var rowsByIndex = new Dictionary<(int ObjectId, int IndexId), IndexRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            await AccumulateIndexRowAsync(reader, rowsByIndex, cancellationToken);
        }

        return BuildIndexesByTable(rowsByIndex);
    }

    private static async Task AccumulateIndexRowAsync(
        SqlDataReader reader,
        Dictionary<(int ObjectId, int IndexId), IndexRow> rowsByIndex,
        CancellationToken cancellationToken)
    {
        var objectId = reader.GetInt32(0);
        var indexId = reader.GetInt32(1);
        var key = (objectId, indexId);

        if (!rowsByIndex.TryGetValue(key, out var row))
        {
            var indexName = await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2);
            var filterDefinition = await reader.IsDBNullAsync(10, cancellationToken) ? null : reader.GetString(10);
            var partitionSchemeName = await reader.IsDBNullAsync(17, cancellationToken) ? null : reader.GetString(17);
            var partitioningColumnName = await reader.IsDBNullAsync(18, cancellationToken) ? null : reader.GetString(18);
            row = new IndexRow(
                Name: indexName,
                TypeDesc: reader.GetString(3),
                IsUnique: reader.GetBoolean(4),
                IsPrimaryKey: reader.GetBoolean(5),
                IsUniqueConstraint: reader.GetBoolean(6),
                HasFilter: reader.GetBoolean(7),
                IsDisabled: reader.GetBoolean(8),
                IsHypothetical: reader.GetBoolean(9),
                KeyColumns: [],
                IncludedColumns: [],
                FilterDefinition: filterDefinition,
                OptimizeForSequentialKey: reader.GetBoolean(11),
                PartitionSchemeName: partitionSchemeName,
                PartitioningColumnName: partitioningColumnName,
                IgnoreDupKey: reader.GetBoolean(19),
                AllowRowLocks: reader.GetBoolean(20),
                AllowPageLocks: reader.GetBoolean(21));
            rowsByIndex[key] = row;
        }

        if (await reader.IsDBNullAsync(15, cancellationToken))
        {

            return;
        }

        var isIncluded = reader.GetBoolean(13);
        var columnName = reader.GetString(15);
        if (isIncluded)
        {
            row.IncludedColumns.Add(columnName);
        }
        else
        {

            var isDescending = reader.GetBoolean(16);
            row.KeyColumns.Add((reader.GetByte(12), columnName, isDescending));
        }
    }

    private static Dictionary<int, List<CatalogIndex>> BuildIndexesByTable(
        Dictionary<(int ObjectId, int IndexId), IndexRow> rowsByIndex)
    {
        var indexesByTable = new Dictionary<int, List<CatalogIndex>>();
        foreach (var ((objectId, _), row) in rowsByIndex)
        {
            var kind = ClassifyIndexKind(row);

            var orderedKeyColumnRows = row.KeyColumns.OrderBy(k => k.Ordinal).ToList();
            var orderedKeyColumns = orderedKeyColumnRows.Select(k => k.Name).ToList();
            var orderedDescendingFlags = orderedKeyColumnRows.Select(k => k.IsDescending).ToList();

            var index = new CatalogIndex(
                Name: row.Name,
                Kind: kind,
                IsUnique: row.IsUnique,
                KeyColumns: orderedKeyColumns,
                IncludedColumns: row.IncludedColumns,
                IsFiltered: row.HasFilter,
                IsColumnstore: row.TypeDesc.Contains("COLUMNSTORE", StringComparison.OrdinalIgnoreCase),
                IsDisabled: row.IsDisabled,
                IsClustered: row.TypeDesc.StartsWith("CLUSTERED", StringComparison.OrdinalIgnoreCase),
                IsHypothetical: row.IsHypothetical,
                FilterDefinition: row.FilterDefinition,
                KeyColumnIsDescendingRaw: orderedKeyColumns.Count > 0 ? orderedDescendingFlags : [],
                OptimizeForSequentialKey: row.OptimizeForSequentialKey,
                PartitionSchemeName: row.PartitionSchemeName,
                PartitioningColumnName: row.PartitioningColumnName,
                IgnoreDupKey: row.IgnoreDupKey,
                IsXmlIndex: string.Equals(row.TypeDesc, "XML", StringComparison.OrdinalIgnoreCase),
                IsSpatialIndex: string.Equals(row.TypeDesc, "SPATIAL", StringComparison.OrdinalIgnoreCase),
                AllowRowLocks: row.AllowRowLocks,
                AllowPageLocks: row.AllowPageLocks);

            if (!indexesByTable.TryGetValue(objectId, out var indexes))
            {
                indexes = [];
                indexesByTable[objectId] = indexes;
            }

            indexes.Add(index);
        }

        return indexesByTable;
    }

    private static async Task<Dictionary<int, List<CatalogStatisticsInfo>>> ReadStatisticsAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.object_id, s.stats_id, s.name, s.no_recompute, s.auto_created, c.name AS column_name
            FROM sys.stats s
            JOIN sys.tables t ON t.object_id = s.object_id
            JOIN sys.stats_columns sc ON sc.object_id = s.object_id AND sc.stats_id = s.stats_id
            JOIN sys.columns c ON c.object_id = sc.object_id AND c.column_id = sc.column_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.object_id, s.stats_id, sc.stats_column_id;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var rowsByStat = new Dictionary<(int ObjectId, int StatsId), (string Name, bool NoRecompute, bool IsAutoCreated, List<string> KeyColumns)>();
        var statOrderByTable = new Dictionary<int, List<(int ObjectId, int StatsId)>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var objectId = reader.GetInt32(0);
            var statsId = reader.GetInt32(1);
            var key = (objectId, statsId);

            if (!rowsByStat.TryGetValue(key, out var row))
            {
                row = (reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), []);
                rowsByStat[key] = row;

                if (!statOrderByTable.TryGetValue(objectId, out var order))
                {
                    order = [];
                    statOrderByTable[objectId] = order;
                }

                order.Add(key);
            }

            row.KeyColumns.Add(reader.GetString(5));
        }

        var statisticsByTable = new Dictionary<int, List<CatalogStatisticsInfo>>();
        foreach (var (objectId, order) in statOrderByTable)
        {
            statisticsByTable[objectId] = order
                .Select(key => rowsByStat[key])
                .Select(row => new CatalogStatisticsInfo(row.Name, row.NoRecompute, row.IsAutoCreated, row.KeyColumns))
                .ToList();
        }

        return statisticsByTable;
    }

    private static async Task<Dictionary<int, (string Name, bool IsReadOnly)>> ReadTableFilegroupsAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT t.object_id, fg.name, fg.is_read_only
            FROM sys.tables t
            JOIN sys.indexes i ON i.object_id = t.object_id AND i.index_id IN (0, 1)
            JOIN sys.filegroups fg ON fg.data_space_id = i.data_space_id
            WHERE t.is_ms_shipped = 0;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var filegroupByTable = new Dictionary<int, (string Name, bool IsReadOnly)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            filegroupByTable[reader.GetInt32(0)] = (reader.GetString(1), reader.GetBoolean(2));
        }

        return filegroupByTable;
    }

    private static async Task<Dictionary<int, string>> ReadTablePartitionSchemesAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT t.object_id, ps.name
            FROM sys.tables t
            JOIN sys.indexes i ON i.object_id = t.object_id AND i.index_id IN (0, 1)
            JOIN sys.partition_schemes ps ON ps.data_space_id = i.data_space_id
            WHERE t.is_ms_shipped = 0;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var schemeByTable = new Dictionary<int, string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            schemeByTable[reader.GetInt32(0)] = reader.GetString(1);
        }

        return schemeByTable;
    }

    private static async Task<List<(string SchemeName, int PartitionNumber, string FilegroupName)>> ReadPartitionFilegroupsAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ps.name, dds.destination_id, fg.name
            FROM sys.partition_schemes ps
            JOIN sys.destination_data_spaces dds ON dds.partition_scheme_id = ps.data_space_id
            JOIN sys.filegroups fg ON fg.data_space_id = dds.data_space_id;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var results = new List<(string, int, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add((reader.GetString(0), reader.GetInt32(1), reader.GetString(2)));
        }

        return results;
    }

    private static async Task<HashSet<int>> ReadRuleConstraintTablesAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DISTINCT c.object_id
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            WHERE t.is_ms_shipped = 0 AND c.rule_object_id <> 0;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var tables = new HashSet<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(reader.GetInt32(0));
        }

        return tables;
    }

    private static async Task<HashSet<int>> ReadFullTextIndexTablesAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = "SELECT object_id FROM sys.fulltext_indexes;";

        await using var command = connection.CreateReadOnlyCommand(sql);

        var tables = new HashSet<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(reader.GetInt32(0));
        }

        return tables;
    }

    private static async Task<HashSet<int>> ReadCdcPartitionSwitchDisallowedTablesAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string isCdcEnabledSql = "SELECT is_cdc_enabled FROM sys.databases WHERE database_id = DB_ID();";
        await using (var probeCommand = connection.CreateReadOnlyCommand(isCdcEnabledSql))
        {
            var isCdcEnabled = await probeCommand.ExecuteScalarAsync(cancellationToken);
            if (isCdcEnabled is not true)
            {
                return [];
            }
        }

        const string sql = """
            SELECT DISTINCT source_object_id
            FROM cdc.change_tables
            WHERE partition_switch = 0;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var tables = new HashSet<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(reader.GetInt32(0));
        }

        return tables;
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
        List<(int Ordinal, string Name, bool IsDescending)> KeyColumns,
        List<string> IncludedColumns,

        bool IsHypothetical = false,

        string? FilterDefinition = null,

        bool OptimizeForSequentialKey = false,

        string? PartitionSchemeName = null,
        string? PartitioningColumnName = null,
        bool IgnoreDupKey = false,
        bool AllowRowLocks = true,
        bool AllowPageLocks = true);

    private static async Task<Dictionary<string, IReadOnlyList<CatalogIndex>>> ReadIndexedViewsAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.name AS schema_name, v.name AS view_name, i.index_id, i.name AS index_name, i.type_desc, i.is_unique,
                   i.is_primary_key, i.is_unique_constraint, i.has_filter, i.is_disabled,
                   ic.key_ordinal, ic.is_included_column, ic.index_column_id, c.name AS column_name
            FROM sys.indexes i
            JOIN sys.views v ON v.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = v.schema_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE v.is_ms_shipped = 0 AND i.type_desc <> 'HEAP'
            ORDER BY s.name, v.name, i.index_id, ic.index_column_id;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var rowsByIndex = new Dictionary<(string QualifiedName, int IndexId), IndexRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var qualifiedName = $"{reader.GetString(0)}.{reader.GetString(1)}";
            var indexId = reader.GetInt32(2);
            var key = (qualifiedName, indexId);

            if (!rowsByIndex.TryGetValue(key, out var row))
            {
                var indexName = await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3);
                row = new IndexRow(
                    Name: indexName,
                    TypeDesc: reader.GetString(4),
                    IsUnique: reader.GetBoolean(5),
                    IsPrimaryKey: reader.GetBoolean(6),
                    IsUniqueConstraint: reader.GetBoolean(7),
                    HasFilter: reader.GetBoolean(8),
                    IsDisabled: reader.GetBoolean(9),
                    KeyColumns: [],
                    IncludedColumns: []);
                rowsByIndex[key] = row;
            }

            var isIncluded = reader.GetBoolean(11);
            var columnName = reader.GetString(13);
            if (isIncluded)
            {
                row.IncludedColumns.Add(columnName);
            }
            else
            {

                row.KeyColumns.Add((reader.GetByte(10), columnName, false));
            }
        }

        var indexesByView = new Dictionary<string, IReadOnlyList<CatalogIndex>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in rowsByIndex.GroupBy(kv => kv.Key.QualifiedName, StringComparer.OrdinalIgnoreCase))
        {
            indexesByView[group.Key] = [.. group.Select(kv => new CatalogIndex(
                Name: kv.Value.Name,
                Kind: ClassifyIndexKind(kv.Value),
                IsUnique: kv.Value.IsUnique,
                KeyColumns: [.. kv.Value.KeyColumns.OrderBy(k => k.Ordinal).Select(k => k.Name)],
                IncludedColumns: kv.Value.IncludedColumns,
                IsFiltered: kv.Value.HasFilter,
                IsColumnstore: kv.Value.TypeDesc.Contains("COLUMNSTORE", StringComparison.OrdinalIgnoreCase),
                IsDisabled: kv.Value.IsDisabled))];
        }

        return indexesByView;
    }

    private static async Task<List<(string QualifiedName, List<string> ColumnNames)>> ReadViewCompiledColumnsAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.name AS schema_name, v.name AS view_name, c.name AS column_name
            FROM sys.columns c
            JOIN sys.views v ON v.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = v.schema_id
            WHERE v.is_ms_shipped = 0
            ORDER BY s.name, v.name, c.column_id;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var byView = new Dictionary<string, (string QualifiedName, List<string> ColumnNames)>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var qualifiedName = $"{reader.GetString(0)}.{reader.GetString(1)}";
            var columnName = reader.GetString(2);

            if (!byView.TryGetValue(qualifiedName, out var entry))
            {
                entry = (qualifiedName, []);
                byView[qualifiedName] = entry;
            }

            entry.ColumnNames.Add(columnName);
        }

        return [.. byView.Values];
    }
}
