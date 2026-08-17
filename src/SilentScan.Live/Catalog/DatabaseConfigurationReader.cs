using Microsoft.Data.SqlClient;
using SilentScan.Core.Predicates;

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

        await using (var command = new SqlCommand(
            """
            SELECT name, page_verify_option_desc, is_auto_shrink_on, is_auto_close_on,
                   target_recovery_time_in_seconds
            FROM sys.databases
            WHERE database_id = DB_ID();
            """, connection))
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

        // sys.database_query_store_options is scoped to the CURRENT database context (the
        // connection string's own initial catalog) - no cross-database join needed, and it
        // returns zero rows on an edition/engine that lacks Query Store entirely (Azure SQL DB
        // vs. on-prem SKUs historically differed here), which this query treats the same as "not
        // read-write" rather than erroring.
        await using (var queryStoreCommand = new SqlCommand(
            "SELECT actual_state_desc, query_capture_mode_desc FROM sys.database_query_store_options;",
            connection))
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
}
