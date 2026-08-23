using Microsoft.Data.SqlClient;
using SilentScan.Core.Predicates;
using SilentScan.Verify.Catalog;

namespace SilentScan.Live.Catalog;

public sealed class DatabaseConfigurationReader
{
    private readonly string _connectionString;

    public DatabaseConfigurationReader(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<DatabaseConfigurationFinding>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        string databaseName;
        string pageVerifyOptionDesc;
        bool isAutoShrinkOn;
        bool isAutoCloseOn;
        int targetRecoveryTimeInSeconds;
        bool isAutoCreateStatsOn;
        bool isAutoUpdateStatsOn;
        int compatibilityLevel;
        int? engineDefaultCompatibilityLevel = null;

        await using (var command = connection.CreateReadOnlyCommand(
            """
            SELECT name, page_verify_option_desc, is_auto_shrink_on, is_auto_close_on,
                   target_recovery_time_in_seconds, is_auto_create_stats_on, is_auto_update_stats_on,
                   compatibility_level
            FROM sys.databases
            WHERE database_id = DB_ID();
            """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return [];
            }

            databaseName = reader.GetString(0);
            pageVerifyOptionDesc = reader.GetString(1);
            isAutoShrinkOn = reader.GetBoolean(2);
            isAutoCloseOn = reader.GetBoolean(3);
            targetRecoveryTimeInSeconds = reader.GetInt32(4);
            isAutoCreateStatsOn = reader.GetBoolean(5);
            isAutoUpdateStatsOn = reader.GetBoolean(6);
            compatibilityLevel = reader.GetByte(7);
        }

        var findings = new List<DatabaseConfigurationFinding>();

        if (!string.Equals(pageVerifyOptionDesc, "CHECKSUM", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.PageVerifyNotChecksum, databaseName));
        }

        if (isAutoShrinkOn)
        {
            findings.Add(new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.AutoShrinkOn, databaseName));
        }

        if (isAutoCloseOn)
        {
            findings.Add(new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.AutoCloseOn, databaseName));
        }

        if (targetRecoveryTimeInSeconds == 0)
        {
            findings.Add(new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.TargetRecoveryTimeUnset, databaseName));
        }

        if (!isAutoCreateStatsOn)
        {
            findings.Add(new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.AutoCreateStatisticsOff, databaseName));
        }

        if (!isAutoUpdateStatsOn)
        {
            findings.Add(new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.AutoUpdateStatisticsOff, databaseName));
        }

        await using (var modelCommand = connection.CreateReadOnlyCommand(
            "SELECT compatibility_level FROM sys.databases WHERE name = 'model';"))
        await using (var modelReader = await modelCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (await modelReader.ReadAsync(cancellationToken))
            {
                engineDefaultCompatibilityLevel = modelReader.GetByte(0);
                if (compatibilityLevel < engineDefaultCompatibilityLevel)
                {
                    findings.Add(new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.CompatibilityLevelBehindEngineDefault, databaseName));
                }
            }

        }

        if (engineDefaultCompatibilityLevel > compatibilityLevel)
        {
            await using var compatibilityCommand = connection.CreateReadOnlyCommand(
                $"""
                SELECT QUOTENAME(OBJECT_SCHEMA_NAME(change_object.major_id)) + N'.' + QUOTENAME(OBJECT_NAME(change_object.major_id)) + COALESCE(N'.' + QUOTENAME(index_row.name), N''), change_object.dependency
                FROM sys.dm_db_objects_disabled_on_compatibility_level_change({engineDefaultCompatibilityLevel.Value}) AS change_object
                LEFT JOIN sys.indexes AS index_row
                    ON index_row.object_id = change_object.major_id
                   AND index_row.index_id = change_object.minor_id
                WHERE change_object.class_desc = N'INDEX'
                  AND (change_object.dependency LIKE N'geography::%' OR change_object.dependency LIKE N'geometry::%')
                ORDER BY change_object.major_id, change_object.minor_id;
                """);

            try
            {
                await using var compatibilityReader = await compatibilityCommand.ExecuteReaderAsync(cancellationToken);
                while (await compatibilityReader.ReadAsync(cancellationToken))
                {
                    findings.Add(new DatabaseConfigurationFinding(
                        DatabaseConfigurationFindingKind.SpatialPersistedComputedColumnDisabledOnCompatibilityLevelChange,
                        databaseName,
                        AffectedObjectName: compatibilityReader.GetString(0),
                        Dependency: compatibilityReader.GetString(1),
                        TargetCompatibilityLevel: engineDefaultCompatibilityLevel));
                }
            }
            catch (SqlException)
            {
            }
        }

        await using (var queryStoreCommand = connection.CreateReadOnlyCommand(
            "SELECT actual_state_desc, query_capture_mode_desc FROM sys.database_query_store_options;"))
        await using (var reader = await queryStoreCommand.ExecuteReaderAsync(cancellationToken))
        {
            var isReadWrite = false;
            if (await reader.ReadAsync(cancellationToken))
            {
                var actualStateDesc = reader.GetString(0);
                var captureModeDesc = reader.GetString(1);
                isReadWrite = string.Equals(actualStateDesc, "READ_WRITE", StringComparison.OrdinalIgnoreCase);

                if (!isReadWrite)
                {
                    findings.Add(new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.QueryStoreNotReadWrite, databaseName));
                }
                else if (!string.Equals(captureModeDesc, "AUTO", StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.QueryStoreCaptureModeNotAuto, databaseName));
                }
            }
            else
            {
                findings.Add(new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.QueryStoreNotReadWrite, databaseName));
            }
        }

        return findings;
    }

public async Task<bool> ReadIsParameterizationForcedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateReadOnlyCommand(
            "SELECT is_parameterization_forced FROM sys.databases WHERE database_id = DB_ID();");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken) && reader.GetBoolean(0);
    }
}
