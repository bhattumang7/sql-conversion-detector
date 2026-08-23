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

        var snapshot = await ReadSnapshotAsync(connection, cancellationToken);
        if (snapshot is null)
        {
            return [];
        }

        var findings = new List<DatabaseConfigurationFinding>();
        AddSnapshotFindings(findings, snapshot);

        var engineDefaultCompatibilityLevel = await AddCompatibilityLevelFindingsAsync(
            connection, snapshot, findings, cancellationToken);

        if (engineDefaultCompatibilityLevel > snapshot.CompatibilityLevel)
        {
            await AddSpatialCompatibilityFindingsAsync(
                connection, snapshot.DatabaseName, engineDefaultCompatibilityLevel.Value, findings, cancellationToken);
        }

        await AddQueryStoreFindingsAsync(connection, snapshot.DatabaseName, findings, cancellationToken);

        return findings;
    }

    private static async Task<DatabaseConfigurationSnapshot?> ReadSnapshotAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateReadOnlyCommand(
            """
            SELECT name, page_verify_option_desc, is_auto_shrink_on, is_auto_close_on,
                   target_recovery_time_in_seconds, is_auto_create_stats_on, is_auto_update_stats_on,
                   compatibility_level
            FROM sys.databases
            WHERE database_id = DB_ID();
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DatabaseConfigurationSnapshot(
            DatabaseName: reader.GetString(0),
            PageVerifyOptionDesc: reader.GetString(1),
            IsAutoShrinkOn: reader.GetBoolean(2),
            IsAutoCloseOn: reader.GetBoolean(3),
            TargetRecoveryTimeInSeconds: reader.GetInt32(4),
            IsAutoCreateStatsOn: reader.GetBoolean(5),
            IsAutoUpdateStatsOn: reader.GetBoolean(6),
            CompatibilityLevel: reader.GetByte(7));
    }

    private static void AddSnapshotFindings(List<DatabaseConfigurationFinding> findings, DatabaseConfigurationSnapshot snapshot)
    {
        if (!string.Equals(snapshot.PageVerifyOptionDesc, "CHECKSUM", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.PageVerifyNotChecksum, snapshot.DatabaseName));
        }

        if (snapshot.IsAutoShrinkOn)
        {
            findings.Add(new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.AutoShrinkOn, snapshot.DatabaseName));
        }

        if (snapshot.IsAutoCloseOn)
        {
            findings.Add(new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.AutoCloseOn, snapshot.DatabaseName));
        }

        if (snapshot.TargetRecoveryTimeInSeconds == 0)
        {
            findings.Add(new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.TargetRecoveryTimeUnset, snapshot.DatabaseName));
        }

        if (!snapshot.IsAutoCreateStatsOn)
        {
            findings.Add(new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.AutoCreateStatisticsOff, snapshot.DatabaseName));
        }

        if (!snapshot.IsAutoUpdateStatsOn)
        {
            findings.Add(new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.AutoUpdateStatisticsOff, snapshot.DatabaseName));
        }
    }

    private static async Task<int?> AddCompatibilityLevelFindingsAsync(
        SqlConnection connection,
        DatabaseConfigurationSnapshot snapshot,
        List<DatabaseConfigurationFinding> findings,
        CancellationToken cancellationToken)
    {
        await using var modelCommand = connection.CreateReadOnlyCommand(
            "SELECT compatibility_level FROM sys.databases WHERE name = 'model';");
        await using var modelReader = await modelCommand.ExecuteReaderAsync(cancellationToken);

        if (!await modelReader.ReadAsync(cancellationToken))
        {
            return null;
        }

        int engineDefaultCompatibilityLevel = modelReader.GetByte(0);
        if (snapshot.CompatibilityLevel < engineDefaultCompatibilityLevel)
        {
            findings.Add(new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.CompatibilityLevelBehindEngineDefault, snapshot.DatabaseName));
        }

        return engineDefaultCompatibilityLevel;
    }

    private static async Task AddSpatialCompatibilityFindingsAsync(
        SqlConnection connection,
        string databaseName,
        int engineDefaultCompatibilityLevel,
        List<DatabaseConfigurationFinding> findings,
        CancellationToken cancellationToken)
    {
        await using var compatibilityCommand = connection.CreateReadOnlyCommand(
            $"""
            SELECT QUOTENAME(OBJECT_SCHEMA_NAME(change_object.major_id)) + N'.' + QUOTENAME(OBJECT_NAME(change_object.major_id)) + COALESCE(N'.' + QUOTENAME(index_row.name), N''), change_object.dependency
            FROM sys.dm_db_objects_disabled_on_compatibility_level_change({engineDefaultCompatibilityLevel}) AS change_object
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
        catch (SqlException ex)
        {
            _ = ex;
        }
    }

    private static async Task AddQueryStoreFindingsAsync(
        SqlConnection connection,
        string databaseName,
        List<DatabaseConfigurationFinding> findings,
        CancellationToken cancellationToken)
    {
        await using var queryStoreCommand = connection.CreateReadOnlyCommand(
            "SELECT actual_state_desc, query_capture_mode_desc FROM sys.database_query_store_options;");
        await using var reader = await queryStoreCommand.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            findings.Add(new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.QueryStoreNotReadWrite, databaseName));
            return;
        }

        var actualStateDesc = reader.GetString(0);
        var captureModeDesc = reader.GetString(1);
        var isReadWrite = string.Equals(actualStateDesc, "READ_WRITE", StringComparison.OrdinalIgnoreCase);

        if (!isReadWrite)
        {
            findings.Add(new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.QueryStoreNotReadWrite, databaseName));
        }
        else if (!string.Equals(captureModeDesc, "AUTO", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.QueryStoreCaptureModeNotAuto, databaseName));
        }
    }

    private sealed record DatabaseConfigurationSnapshot(
        string DatabaseName,
        string PageVerifyOptionDesc,
        bool IsAutoShrinkOn,
        bool IsAutoCloseOn,
        int TargetRecoveryTimeInSeconds,
        bool IsAutoCreateStatsOn,
        bool IsAutoUpdateStatsOn,
        int CompatibilityLevel);

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
