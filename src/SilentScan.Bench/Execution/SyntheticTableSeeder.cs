using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SilentScan.Bench.Scenarios;

namespace SilentScan.Bench.Execution;

public static partial class SyntheticTableSeeder
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex ValidIdentifier();

    public static async Task SeedAsync(SqlConnection connection, TypePairScenario scenario, string tableName, int rowCount, CancellationToken cancellationToken = default)
    {

        if (!ValidIdentifier().IsMatch(tableName))
        {
            throw new ArgumentException($"'{tableName}' is not a safe SQL identifier for a synthetic table name.", nameof(tableName));
        }

        await using (var createCommand = connection.CreateCommand())
        {
            createCommand.CommandText = $"""
                DROP TABLE IF EXISTS dbo.{tableName};
                CREATE TABLE dbo.{tableName}
                (
                    Id   INT              NOT NULL PRIMARY KEY,
                    Code {scenario.ColumnTypeDdl} NOT NULL
                );
                """;
            createCommand.CommandTimeout = 60;
            await createCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var seedCommand = connection.CreateCommand())
        {
            seedCommand.CommandText = $"""
                ;WITH Tally AS
                (
                    SELECT TOP ({rowCount.ToString(CultureInfo.InvariantCulture)}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                    FROM sys.all_columns a CROSS JOIN sys.all_columns b
                )
                INSERT INTO dbo.{tableName} (Id, Code)
                SELECT n, {scenario.SeedValueExpression} FROM Tally;
                """;
            seedCommand.CommandTimeout = 300;
            await seedCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = $"CREATE INDEX IX_{tableName}_Code ON dbo.{tableName}(Code);";
        indexCommand.CommandTimeout = 300;
        await indexCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
