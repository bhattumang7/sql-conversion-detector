using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SilentScan.Verify.Deployment;

/// <summary>
/// Creates and tears down the disposable per-run databases the Verify oracle deploys corpus
/// DDL into (CLAUDE.md: "deploy its DDL to a fresh database"). Compat level is pinned to 160
/// to match the Bench/CE policy.
/// </summary>
public sealed partial class DatabaseProvisioner
{
    private const int PinnedCompatibilityLevel = 160;

    private readonly SqlServerOptions _options;

    public DatabaseProvisioner(SqlServerOptions options)
    {
        _options = options;
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex ValidIdentifier();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{2,127}$")]
    private static partial Regex ValidCollationName();

    /// <summary>
    /// Drops <paramref name="databaseName"/> if it exists, then creates it fresh at the pinned
    /// compat level. When <paramref name="collationName"/> is supplied, the database is created
    /// WITH that collation rather than the instance default - the static classifier types
    /// corpus columns using the manifest's declared collation (CLAUDE.md corpus rules), so the
    /// deployed environment must agree with it or the oracle probes a different collation family
    /// than the one the verdict was reasoned about, which can silently confirm the wrong
    /// direction (e.g. a Windows-collation RangeSeek finding probed against a SQL_* instance
    /// default reads back as a false ScanForced confirmation).
    /// </summary>
    public async Task CreateFreshAsync(string databaseName, string? collationName = null, CancellationToken cancellationToken = default)
    {
        if (collationName is not null && !ValidCollationName().IsMatch(collationName))
        {
            throw new ArgumentException($"'{collationName}' is not a safe SQL Server collation name.", nameof(collationName));
        }

        await using var connection = await OpenMasterConnectionAsync(databaseName, cancellationToken);

        await DropIfExistsCoreAsync(connection, databaseName, cancellationToken);
        var collateClause = collationName is null ? string.Empty : $" COLLATE {collationName}";
        await ExecuteAsync(connection, $"CREATE DATABASE [{databaseName}]{collateClause};", cancellationToken);
        await ExecuteAsync(connection, $"ALTER DATABASE [{databaseName}] SET COMPATIBILITY_LEVEL = {PinnedCompatibilityLevel};", cancellationToken);
    }

    /// <summary>Drops <paramref name="databaseName"/> if it exists. Safe to call even if creation never succeeded.</summary>
    public async Task DropIfExistsAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenMasterConnectionAsync(databaseName, cancellationToken);

        await DropIfExistsCoreAsync(connection, databaseName, cancellationToken);
    }

    private async Task<SqlConnection> OpenMasterConnectionAsync(string databaseName, CancellationToken cancellationToken)
    {
        // Database names can't be parameterized in DDL; this is the only defense against
        // injecting through this identifier, and it's a hard requirement since these
        // statements are string-built, not sent as SqlParameters.
        if (!ValidIdentifier().IsMatch(databaseName))
        {
            throw new ArgumentException($"'{databaseName}' is not a safe SQL identifier for a disposable database name.", nameof(databaseName));
        }

        var connection = new SqlConnection(_options.BuildConnectionString(database: null));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static Task DropIfExistsCoreAsync(SqlConnection connection, string databaseName, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, $"""
            IF DB_ID(N'{databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{databaseName}];
            END
            """, cancellationToken);

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
