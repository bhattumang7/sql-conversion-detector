using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Verify.Catalog;

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

        catalog.CurrentDatabaseName = connection.Database;
        catalog.DefaultCollation = await ReadDatabaseDefaultCollationAsync(connection, cancellationToken);
        catalog.CompatibilityLevel = await ReadCompatibilityLevelAsync(connection, cancellationToken);
        catalog.IsRecursiveTriggersEnabled = await ReadIsRecursiveTriggersEnabledAsync(connection, cancellationToken);
        catalog.IsNestedTriggersEnabled = await ReadIsNestedTriggersEnabledAsync(connection, cancellationToken);

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
                PartitionSchemeName: partitionSchemeByTable.GetValueOrDefault(objectId)));
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

    /// <summary>
    /// T-SQL scalar UDFs (<c>sys.objects.type = 'FN'</c>): <c>is_schema_bound</c> and (2019+ only)
    /// <c>is_inlineable</c>, both straight from <c>sys.sql_modules</c> - the engine's own
    /// authoritative answer, always preferred over the static blocker scan
    /// (<see cref="Core.Catalog.ScalarUdfInlineabilityScanner"/>) that runs later, in file mode,
    /// over this same function's reparsed body text (<c>LiveModuleReader</c> already fetches an
    /// 'FN' object's <c>sys.sql_modules.definition</c>). <c>is_inlineable</c> doesn't exist on
    /// pre-2019 engines - queried first WITH it, falling back to a query WITHOUT it on "invalid
    /// column name" (error 207) rather than a version-number check, so this stays correct against
    /// whatever the connected engine actually exposes rather than an assumption about it.
    /// </summary>
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

    /// <summary>
    /// CLR scalar UDFs (<c>sys.objects.type = 'FS'</c>): never inlined, so what matters is
    /// whether the assembly method touches data - <c>OBJECTPROPERTYEX(..., 'UserDataAccess')</c>/
    /// <c>'SystemDataAccess'</c> report the <c>DataAccessKind</c> the method was registered with
    /// (either true forces the same serial-plan consequence a T-SQL UDF's non-inlineability does).
    /// Queried ONE FUNCTION AT A TIME rather than in the single bulk SELECT every other reader in
    /// this file uses - oracle-verified against the local production copy's real EXTERNAL_ACCESS
    /// assemblies (CLAUDE.md never waits for permission to run this check): calling
    /// <c>OBJECTPROPERTYEX</c> on a function whose assembly cannot currently be loaded (blocked
    /// permission set, missing/changed assembly, etc.) throws SqlException 10342 and aborts the
    /// WHOLE batch, not just that one row - a single such function in a real corpus would
    /// otherwise blank out every OTHER CLR scalar UDF's data-access info too. A per-function
    /// failure is recorded as ClrDataAccess = null (unknown), never guessed, and never allowed to
    /// take down the rest of the read.
    /// </summary>
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

    /// <summary>
    /// Which flavour each user table-valued function is, straight from <c>sys.objects.type</c>.
    /// This is the engine's own classification and the only authoritative source for it: a call
    /// site (<c>FROM dbo.fn(@x)</c>) is textually identical for an inline TVF and a
    /// multi-statement one, and the MSTVF-as-fence stream depends entirely on telling them
    /// apart. A type code this method does not recognise is skipped rather than defaulted, so
    /// an unmapped future object type can never be reported as either flavour.
    /// </summary>
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

    /// <summary>
    /// A real table's own computed-column/DEFAULT/CHECK definitions, straight from
    /// <c>sys.computed_columns</c>/<c>sys.default_constraints</c>/<c>sys.check_constraints</c> -
    /// the live-authoritative source for the scalar-UDF SchemaDependency stream
    /// (<see cref="SilentScan.Core.Predicates.SchemaDependencyScanner"/>), which reparses each
    /// definition's own text the same way regardless of whether it arrived from here or from
    /// <see cref="SchemaExpressionCollector"/>'s file-mode capture. Table-level CHECK
    /// constraints (no single owning column) are queried separately from column-level ones, since
    /// <c>sys.check_constraints.parent_column_id</c> is 0 for a table-level constraint and this
    /// reader reports that case with a null column name rather than a meaningless "column 0".
    /// </summary>
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

    /// <summary>
    /// Every foreign-key column pair, engine-authoritative (docs/detection-checklist.md Tier 1
    /// "Join-key and cross-object type/collation mismatch": FK-linked pairs for the cross-table
    /// type-drift report). A composite FK produces one row per column pair here, all sharing the
    /// same constraint name - <see cref="SilentScan.Core.Catalog.ForeignKeyRelationship"/> keeps
    /// them as separate entries rather than grouping, matching how <c>sys.foreign_key_columns</c>
    /// itself represents a composite key: one row per (parent column, referenced column) pair.
    /// </summary>
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

    /// <summary>
    /// docs/detection-checklist.md practitioner-sweep item "Row-Level Security predicate function
    /// with no supporting index on its own filtered columns" - see <see
    /// cref="SilentScan.Core.Catalog.CatalogSecurityPredicate"/>'s own doc comment for why
    /// <c>predicate_definition</c> (not a dedicated function-id column - there isn't one) is the
    /// only catalog-level way to recover the predicate function's own bound columns, and <see
    /// cref="SilentScan.Core.Predicates.SecurityPredicateIndexFinding"/> for the full scope/oracle
    /// story.
    /// <c>sys.security_policies.is_ms_shipped</c> does not exist (confirmed directly against the
    /// standing Docker oracle - unlike <c>sys.tables</c>, a security policy has no shipped/system
    /// variant to exclude), so only the secured TABLE side is filtered on
    /// <c>is_ms_shipped = 0</c>, matching every other reader in this file.
    /// </summary>
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

    /// <summary>
    /// Every system-versioned temporal table's own current/history pairing, read from
    /// <c>sys.tables.temporal_type = 2</c> (<c>SYSTEM_VERSIONED_TEMPORAL_TABLE</c>) joined to its
    /// own <c>history_table_id</c>. Both sides are ALSO ordinary rows in <see cref="ReadTablesAsync"/>/
    /// <see cref="ReadIndexesAsync"/> already - a history table has no distinct
    /// <c>sys.objects.type</c>, it's a plain user table with <c>temporal_type = 1</c> - so this
    /// query supplies only the pairing fact, not table/index shape (docs/detection-checklist.md
    /// "Temporal table history-side index gap").
    /// </summary>
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

    /// <summary>
    /// A SQLCLR table-valued function's return-table columns, read from <c>sys.columns</c>
    /// keyed off <c>sys.objects.type = 'FT'</c> (assembly TVF) rather than the ordinary
    /// <c>sys.tables</c> join <see cref="ReadColumnsAsync"/> uses - there is no
    /// <c>sys.sql_modules</c> body to parse for one of these, but the engine still exposes its
    /// return shape as real column metadata, so a caller referencing it in a FROM clause can be
    /// typed exactly like a real table even though the function body itself stays unanalyzable.
    /// </summary>
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

    /// <summary>
    /// A SQLCLR scalar function's return type, read from <c>sys.parameters</c> at
    /// <c>parameter_id = 0</c> (the standard way the engine exposes any function's own return
    /// type, CLR or T-SQL) keyed off <c>sys.objects.type = 'FS'</c> - the ordinary scalar-UDF
    /// return-type table (<see cref="DatabaseCatalog.AddScalarFunctionReturnType"/>) is otherwise
    /// populated only from a parsed <c>CREATE FUNCTION</c> body, which a CLR function has none of.
    /// Unlike <c>sys.columns</c>, <c>sys.parameters</c> carries no <c>collation_name</c> column
    /// at all (verified against the real engine - querying one throws "Invalid column name"), so
    /// <paramref name="databaseDefaultCollationName"/> is used for any string-family return type
    /// instead. This is not a guess: a SQLCLR scalar function has no way to declare a COLLATE on
    /// its own return value, so its string output collation is always the connected database's
    /// default - oracle-verified directly (<c>sys.dm_exec_describe_first_result_set</c> against a
    /// real deployed CLR scalar function returning nvarchar reported exactly the database's own
    /// default collation, nothing else).
    /// </summary>
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

    /// <summary>
    /// Server-level <c>sys.configurations</c> option, distinct from the database-level
    /// <c>RECURSIVE_TRIGGERS</c> option read by <see cref="ReadIsRecursiveTriggersEnabledAsync"/> -
    /// see <see cref="DatabaseCatalog.IsNestedTriggersEnabled"/> for the oracle-confirmed
    /// distinction. <c>value_in_use</c> (not <c>value</c>) is the running value - a changed
    /// <c>value</c> only takes effect after <c>RECONFIGURE</c>, which <c>value_in_use</c> already
    /// reflects.
    /// </summary>
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

    /// <summary>
    /// Roadmap Phase C2: <c>CREATE SYNONYM</c> is DDL a live scan never parses text for (a
    /// synonym is metadata only, never a <c>sys.sql_modules</c> body <see cref="LiveModuleReader"/>
    /// reads), so without this a synonym-qualified FROM-clause reference in a live-scanned
    /// module always resolved "no known DDL", identically to a file-mode scan that happened to
    /// omit the synonym's own script. <c>base_object_name</c> is the exact text as declared
    /// (bracket-quoted or not, 1-4 parts) - parsed the same way any other schema object name in
    /// this codebase is, rather than hand-rolling a second bracket-stripping normalizer.
    /// </summary>
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

    /// <summary>Parses a raw schema-object-name string (as `sys.synonyms.base_object_name` reports it) into the same qualified form <see cref="SchemaObjectNameHelper.Qualify"/> produces elsewhere, via a throwaway wrapper statement rather than a second hand-rolled bracket/part parser.</summary>
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
        // LEFT JOINs sys.identity_columns for CatalogColumn's own IdentitySeed/IdentityIncrement/
        // IdentityCurrentValue (docs/detection-checklist.md "DBA-script family sweep" §A
        // "Identity/sequence range exhaustion") - a single extra join on the same query every
        // other column fact already comes from, not a separate live round trip. seed_value/
        // increment_value/last_value are sql_variant at the engine level; CONVERTed to
        // decimal(38,0) here so every identity type (tinyint through decimal(p,0)) reports through
        // the same CatalogColumn shape rather than needing a per-type reader downstream. last_value
        // is NULL until the first row is ever inserted - reported as null (never a guessed 0),
        // matching CatalogColumn's own "never guess" default for every other unresolved fact.
        const string sql = """
            SELECT c.object_id, s.name AS schema_name, t.name AS table_name, c.name AS column_name,
                   ty.name AS type_name, c.max_length, c.precision, c.scale, c.collation_name,
                   c.is_nullable, c.is_identity, c.is_computed, cc.is_persisted, c.is_ansi_padded,
                   CONVERT(decimal(38,0), idc.seed_value), CONVERT(decimal(38,0), idc.increment_value),
                   CONVERT(decimal(38,0), idc.last_value), c.encryption_type
            FROM sys.columns c
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN sys.identity_columns idc ON idc.object_id = c.object_id AND idc.column_id = c.column_id
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
            var isAnsiPadded = reader.GetBoolean(13);
            var identitySeed = await reader.IsDBNullAsync(14, cancellationToken) ? (decimal?)null : reader.GetDecimal(14);
            var identityIncrement = await reader.IsDBNullAsync(15, cancellationToken) ? (decimal?)null : reader.GetDecimal(15);
            var identityCurrentValue = await reader.IsDBNullAsync(16, cancellationToken) ? (decimal?)null : reader.GetDecimal(16);
            var encryptionType = await reader.IsDBNullAsync(17, cancellationToken)
                ? SilentScan.Core.Catalog.ColumnEncryptionType.None
                : reader.GetInt32(17) switch
                {
                    1 => SilentScan.Core.Catalog.ColumnEncryptionType.Deterministic,
                    2 => SilentScan.Core.Catalog.ColumnEncryptionType.Randomized,
                    _ => SilentScan.Core.Catalog.ColumnEncryptionType.None,
                };

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

            columns.Add(new CatalogColumn(
                columnName, type, isNullable, isIdentity, isComputed, isPersisted, isAnsiPadded,
                identitySeed, identityIncrement, identityCurrentValue, encryptionType));
        }

        return columnsByTable;
    }

    private static async Task<Dictionary<int, List<CatalogIndex>>> ReadIndexesAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        // LEFT JOINs to sys.index_columns/sys.columns, not the INNER JOINs every other reader in
        // this file uses - a clustered COLUMNSTORE index (type_desc = 'CLUSTERED COLUMNSTORE')
        // owns every column implicitly and has NO sys.index_columns rows of its own, so an INNER
        // JOIN here would silently drop the whole index row. That would have been invisible
        // before CatalogIndex.IsClustered existed (nothing looked for a clustered index's
        // presence at all), but IndexDesignScanner's heap findings depend on it: a table whose
        // only clustering is a CCI must never be misread as a heap because its one clustered
        // index vanished from this list. ic.index_column_id/c.name are null for such a row - the
        // loop below guards both.
        // ds/pc join for docs/detection-checklist.md "Non-aligned index on a partitioned table" -
        // ds.name only counts as a partition scheme when ds.type = 'PS' (a plain single-filegroup
        // index's own data_space_id points at an 'FG' row instead, deliberately read as no scheme
        // name at all rather than the filegroup's own name - see CatalogIndex.PartitionSchemeName's
        // own doc comment); pc.name is the column at partition_ordinal = 1 for THIS index's own
        // sys.index_columns rows, confirmed directly against the standing Docker instance
        // (2026-08-20) to differ from the table's own partitioning column even when two indexes
        // share the identical partition scheme object.
        const string sql = """
            SELECT i.object_id, i.index_id, i.name AS index_name, i.type_desc, i.is_unique,
                   i.is_primary_key, i.is_unique_constraint, i.has_filter, i.is_disabled,
                   i.is_hypothetical, i.filter_definition, i.optimize_for_sequential_key,
                   ic.key_ordinal, ic.is_included_column, ic.index_column_id, c.name AS column_name,
                   ic.is_descending_key,
                   CASE WHEN ds.type = 'PS' THEN ds.name ELSE NULL END AS partition_scheme_name,
                   pc.name AS partitioning_column_name, i.ignore_dup_key
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
                IgnoreDupKey: reader.GetBoolean(19));
            rowsByIndex[key] = row;
        }

        if (await reader.IsDBNullAsync(15, cancellationToken))
        {
            // No sys.index_columns row at all (clustered columnstore) - the index row above
            // was already recorded with empty key/included lists; nothing more to add.
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
            // is_descending_key is meaningless (but non-null, always false) for an included
            // column - only read it on the key-column branch, matching the shape KeyColumns
            // itself already takes.
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
                IsSpatialIndex: string.Equals(row.TypeDesc, "SPATIAL", StringComparison.OrdinalIgnoreCase));

            if (!indexesByTable.TryGetValue(objectId, out var indexes))
            {
                indexes = [];
                indexesByTable[objectId] = indexes;
            }

            indexes.Add(index);
        }

        return indexesByTable;
    }

    /// <summary>
    /// <c>sys.stats</c> - a distinct catalog view from <c>sys.indexes</c>: every index owns a
    /// matching stats object implicitly, but the engine also auto-creates single-column stats
    /// with no backing index at all, and <c>sys.stats</c> is the only place either kind's own
    /// <c>no_recompute</c> flag lives. <c>docs/detection-checklist.md</c> "DBA-script family
    /// sweep" §A "Statistics-object flags" - a stats object explicitly marked
    /// <c>NORECOMPUTE</c> never gets its cardinality estimate refreshed by the engine's own
    /// automatic maintenance, silently drifting stale as the table's data changes.
    /// </summary>
    private static async Task<Dictionary<int, List<CatalogStatisticsInfo>>> ReadStatisticsAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT s.object_id, s.name, s.no_recompute, s.auto_created
            FROM sys.stats s
            JOIN sys.tables t ON t.object_id = s.object_id
            WHERE t.is_ms_shipped = 0;
            """;

        await using var command = connection.CreateReadOnlyCommand(sql);

        var statisticsByTable = new Dictionary<int, List<CatalogStatisticsInfo>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var objectId = reader.GetInt32(0);
            var info = new CatalogStatisticsInfo(
                Name: reader.GetString(1),
                NoRecompute: reader.GetBoolean(2),
                IsAutoCreated: reader.GetBoolean(3));

            if (!statisticsByTable.TryGetValue(objectId, out var list))
            {
                list = [];
                statisticsByTable[objectId] = list;
            }

            list.Add(info);
        }

        return statisticsByTable;
    }

    /// <summary>
    /// A table's own storage filegroup (name plus current read-only state), joined off its heap/
    /// clustered-index row (<c>sys.indexes.index_id IN (0, 1)</c> - 0 is a heap, 1 is the clustered
    /// index; a table has exactly one such row). Deliberately an INNER JOIN to
    /// <c>sys.filegroups</c>, not a LEFT JOIN to <c>sys.data_spaces</c> the way
    /// <see cref="ReadIndexesAsync"/>'s partition-scheme columns are: a PARTITIONED table's
    /// heap/clustered-index row points at a partition SCHEME's own <c>data_space_id</c>, which
    /// never appears in <c>sys.filegroups</c> at all, so the INNER JOIN naturally excludes
    /// partitioned tables from this result rather than misreporting a partition scheme as a
    /// filegroup name - see <see cref="CatalogTable.FilegroupName"/>'s own doc comment for why
    /// that's the correct, deliberate scope limit.
    /// </summary>
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

    /// <summary>
    /// The partition scheme a table's own heap/clustered-index row (<c>sys.indexes.index_id IN
    /// (0, 1)</c>) belongs to - the partitioned-table counterpart to
    /// <see cref="ReadTableFilegroupsAsync"/>'s non-partitioned case, joined against
    /// <c>sys.partition_schemes</c> instead of <c>sys.filegroups</c> so the two queries never both
    /// match the same table (a table's storage <c>data_space_id</c> is either a scheme or a
    /// filegroup, never both). See <see cref="CatalogTable.PartitionSchemeName"/>'s own doc
    /// comment.
    /// </summary>
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

    /// <summary>
    /// A partition scheme's static per-partition-number filegroup assignment
    /// (<c>sys.destination_data_spaces</c>) - see <see cref="SilentScan.Core.Catalog.DatabaseCatalog.AddPartitionFilegroup"/>'s
    /// own doc comment.
    /// </summary>
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

    /// <summary>
    /// Tables carrying at least one legacy <c>CREATE RULE</c>/<c>sp_bindrule</c> column binding
    /// (<c>sys.columns.rule_object_id &lt;&gt; 0</c>) - distinct from a modern CHECK constraint,
    /// which is <see cref="CatalogCheckConstraint"/>'s own concern. Oracle-confirmed (2026-08-21)
    /// as an <c>ALTER TABLE ... SWITCH</c> blocker - see <see cref="CatalogTable.HasRuleConstraint"/>'s
    /// own doc comment.
    /// </summary>
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

    /// <summary>
    /// Tables carrying at least one CDC capture instance whose own <c>@allow_partition_switch</c>
    /// was explicitly set to 0 (<c>cdc.change_tables.partition_switch = 0</c>) - oracle-confirmed
    /// as an <c>ALTER TABLE ... SWITCH PARTITION</c> blocker for a genuinely partitioned table,
    /// see <see cref="CatalogTable.CdcPartitionSwitchDisallowed"/>'s own doc comment. The
    /// <c>cdc</c> schema only exists once CDC has been enabled for the current database at all
    /// (<c>sp_cdc_enable_db</c>) - querying <c>cdc.change_tables</c> on a database that never
    /// enabled CDC would fail outright, so this checks <c>sys.databases.is_cdc_enabled</c> first
    /// and returns an empty set without querying further when it's off (the overwhelming common
    /// case).
    /// </summary>
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
        // Only ReadIndexesAsync's own query reads is_hypothetical - ReadIndexedViewsAsync shares
        // this same row shape but never sets it (an indexed view's own index is never a DTA/
        // missing-index-wizard hypothetical row), so it defaults to false rather than every other
        // caller needing to pass it explicitly.
        bool IsHypothetical = false,
        // Same reasoning as IsHypothetical above - only ReadIndexesAsync's own query reads
        // filter_definition; ReadIndexedViewsAsync never sets it.
        string? FilterDefinition = null,
        // Same reasoning again - only ReadIndexesAsync's own query reads
        // optimize_for_sequential_key; ReadIndexedViewsAsync never sets it (an indexed view's own
        // clustered index is never the "monotonic clustered key" shape this field exists for).
        bool OptimizeForSequentialKey = false,
        // Same reasoning again - only ReadIndexesAsync's own query reads the partition-scheme/
        // partitioning-column pair; ReadIndexedViewsAsync never sets either (an indexed view is
        // never itself a partitioned base table).
        string? PartitionSchemeName = null,
        string? PartitioningColumnName = null,
        // Same reasoning again - only ReadIndexesAsync's own query reads ignore_dup_key;
        // ReadIndexedViewsAsync never sets it (IGNORE_DUP_KEY is a unique-index-only option, and
        // an indexed view's own clustered index is never the target of a plain INSERT the way a
        // base table is).
        bool IgnoreDupKey = false);

    /// <summary>
    /// The same shape as <see cref="ReadIndexesAsync"/>, joined against <c>sys.views</c> instead
    /// of <c>sys.tables</c> (docs/detection-checklist.md Tier 1 "SET options that silently
    /// disable plan features" - both the QUOTED_IDENTIFIER/NUMERIC_ROUNDABORT and ARITHABORT
    /// sub-rules need to know whether a query touches an indexed view). A view's own real
    /// clustered index (created via <c>CREATE UNIQUE CLUSTERED INDEX ... ON dbo.SomeView</c>) is
    /// the one thing that makes it "indexed" at all - an ordinary view has no
    /// <c>sys.indexes</c> row of its own.
    /// </summary>
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
                // This query never reads is_descending_key - indexed views don't participate in
                // the merge-candidate check that field exists for, so IsDescending is always false
                // here rather than a real value.
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

    /// <summary>
    /// Every user view's own <c>sys.columns</c> row set, in <c>column_id</c> order - the engine's
    /// real, currently-cached column list for the view object (docs/detection-checklist.md
    /// "Second full-archive practitioner sweep" §G: "View defined with SELECT * whose compiled
    /// column list has gone stale against the base table's current shape"). A view has its own
    /// real <c>sys.columns</c> rows exactly like a table does - this is a separate query from
    /// <see cref="ReadColumnsAsync"/> (which joins <c>sys.tables</c>, not <c>sys.views</c>) rather
    /// than folding views into that same read, since a view's columns are never attached to a
    /// <see cref="CatalogTable"/> in this codebase's model (mirrors <see cref="ReadIndexedViewsAsync"/>'s
    /// own reasoning for keeping an indexed view's index shape as a side-registry rather than a
    /// <see cref="CatalogTable"/> row).
    /// </summary>
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
