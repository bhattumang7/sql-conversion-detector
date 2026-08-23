using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SilentScan.Verify.Deployment;

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

        await ExecuteAsync(connection, $"ALTER DATABASE [{databaseName}] SET QUERY_STORE = OFF;", cancellationToken);
    }

    public async Task DropIfExistsAsync(string databaseName, CancellationToken cancellationToken = default)
    {

        using var targetConnection = new SqlConnection(_options.BuildConnectionString(databaseName));
        SqlConnection.ClearPool(targetConnection);

        await using var connection = await OpenMasterConnectionAsync(databaseName, cancellationToken);

        await DropIfExistsCoreAsync(connection, databaseName, cancellationToken);
    }

    private async Task<SqlConnection> OpenMasterConnectionAsync(string databaseName, CancellationToken cancellationToken)
    {

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
