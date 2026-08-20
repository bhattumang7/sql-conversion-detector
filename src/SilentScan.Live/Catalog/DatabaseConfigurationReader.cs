using Microsoft.Data.SqlClient;
using SilentScan.Core.Predicates;
using SilentScan.Verify.Catalog;

namespace SilentScan.Live.Catalog;

/// <summary>
/// docs/detection-checklist.md "Second OSS/commercial sweep": "Database-level configuration
/// flags" - a single, cheap read of the target database's own row in <c>sys.databases</c> plus
/// (when relevant) <c>sys.database_query_store_options</c>, run once per scan (not per module).
/// See <see cref="DatabaseConfigurationFinding"/> for the full per-flag reasoning and severity
/// split.
/// </summary>
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
                // The connecting login cannot see its own database's row in sys.databases at all
                // (an unusually locked-down permission set) - never guess, report nothing.
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

        // "The engine's own current default compat level" is read live from the model system
        // database (see DatabaseConfigurationFinding's own doc comment for why this is preferred
        // over a SERVERPROPERTY('ProductMajorVersion')-derived version-number mapping) - model is
        // an unqualified, server-scoped sys.databases row visible from any database's connection,
        // no USE/context switch required, and is exactly what the engine itself clones every newly
        // created database from.
        await using (var modelCommand = connection.CreateReadOnlyCommand(
            "SELECT compatibility_level FROM sys.databases WHERE name = 'model';"))
        await using (var modelReader = await modelCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (await modelReader.ReadAsync(cancellationToken))
            {
                var engineDefaultCompatibilityLevel = modelReader.GetByte(0);
                if (compatibilityLevel < engineDefaultCompatibilityLevel)
                {
                    findings.Add(new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.CompatibilityLevelBehindEngineDefault, databaseName));
                }
            }

            // If model's own row is unreadable (an unusually locked-down permission set that
            // still allowed the earlier DB_ID() row through), never guess at the engine's default -
            // silently skip this one kind rather than report a comparison against a made-up level.
        }

        // sys.database_query_store_options is scoped to the CURRENT database context (the
        // connection string's own initial catalog) - no cross-database join needed, and it
        // returns zero rows on an edition/engine that lacks Query Store entirely (Azure SQL DB
        // vs. on-prem SKUs historically differed here), which this query treats the same as "not
        // read-write" rather than erroring.
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
                    // Only evaluated when Query Store IS actually running - see
                    // DatabaseConfigurationFinding's own doc comment for why.
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

    /// <summary>
    /// docs/detection-reference.md Appendix 8's forced-parameterization clause-skip entry - the
    /// single live precondition <see cref="ForcedParameterizationScanner"/>'s own AST
    /// findings are gated on. A separate, tiny round trip rather than folded into
    /// <see cref="ReadAsync"/>'s own query: it isn't a <see cref="DatabaseConfigurationFinding"/>
    /// itself (a database explicitly turning this ON is a deliberate choice, not a misconfiguration -
    /// see docs/detection-reference.md's "Forced plans / plan guides / forced parameterization"
    /// survey entry, correctly skipped), only a fact another stream needs to know before it can
    /// report anything at all.
    /// </summary>
    public async Task<bool> ReadIsParameterizationForcedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateReadOnlyCommand(
            "SELECT is_parameterization_forced FROM sys.databases WHERE database_id = DB_ID();");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // No visible row at all (an unusually locked-down permission set) - never guess; treat
        // as "not forced" so the gated scanner stays silent rather than assuming a state it
        // can't confirm.
        return await reader.ReadAsync(cancellationToken) && reader.GetBoolean(0);
    }
}
