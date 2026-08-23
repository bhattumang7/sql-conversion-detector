using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class MemoryOptimizedOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(MemoryOptimizedOracleTests);

    protected override string Ddl =>
        """
        DECLARE @dataDir NVARCHAR(260) = (
            SELECT LEFT(physical_name, LEN(physical_name) - CHARINDEX('/', REVERSE(physical_name)) + 1)
            FROM sys.master_files WHERE database_id = DB_ID() AND file_id = 1);
        DECLARE @sql NVARCHAR(MAX) = N'
            ALTER DATABASE CURRENT ADD FILEGROUP MemoryOptimizedFg CONTAINS MEMORY_OPTIMIZED_DATA;
            ALTER DATABASE CURRENT ADD FILE (name=''MemoryOptimizedFile'', filename=''' + @dataDir + N'memopt_oracle'') TO FILEGROUP MemoryOptimizedFg;';
        EXEC sp_executesql @sql;
        """;

    private async Task<SqlException> AssertDeployFailsAsync(string ddl)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(ddl, connection);
        return await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
    }

    private async Task AssertDeploySucceedsAsync(string ddl)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(ddl, connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task XmlColumn_OnMemoryOptimizedTable_FailsToDeploy()
    {
        var exception = await AssertDeployFailsAsync(
            """
            CREATE TABLE dbo.Widgets (Id INT NOT NULL PRIMARY KEY NONCLUSTERED, Tag XML NULL) WITH (MEMORY_OPTIMIZED = ON);
            """);

        Assert.Equal(10794, exception.Number);
    }

    [Fact]
    public async Task SameColumnType_OnOrdinaryTable_DeploysCleanly()
    {
        await AssertDeploySucceedsAsync(
            """
            CREATE TABLE dbo.Widgets (Id INT NOT NULL PRIMARY KEY, Tag XML NULL);
            """);
    }

    [Fact]
    public async Task IncludedColumns_OnMemoryOptimizedIndex_FailsToDeploy()
    {
        var exception = await AssertDeployFailsAsync(
            """
            CREATE TABLE dbo.Widgets (
                Id INT NOT NULL PRIMARY KEY NONCLUSTERED,
                Amount INT NULL,
                Note NVARCHAR(50) NULL,
                INDEX IX_Amount NONCLUSTERED (Amount) INCLUDE (Note)
            ) WITH (MEMORY_OPTIMIZED = ON);
            """);

        Assert.Equal(10664, exception.Number);
    }

    [Fact]
    public async Task ClusteredPrimaryKey_OnMemoryOptimizedTable_FailsToDeploy()
    {
        var exception = await AssertDeployFailsAsync(
            """
            CREATE TABLE dbo.Widgets (Id INT NOT NULL PRIMARY KEY) WITH (MEMORY_OPTIMIZED = ON);
            """);

        Assert.Equal(12317, exception.Number);
    }

    [Fact]
    public async Task ForeignKey_SpanningMemoryOptimizedAndDiskBasedTables_FailsToDeploy()
    {
        await AssertDeploySucceedsAsync(
            """
            CREATE TABLE dbo.Orders (Id INT NOT NULL PRIMARY KEY);
            """);

        var exception = await AssertDeployFailsAsync(
            """
            CREATE TABLE dbo.Lines (
                Id INT NOT NULL PRIMARY KEY NONCLUSTERED,
                OrderId INT NOT NULL,
                CONSTRAINT FK_Lines_Orders FOREIGN KEY (OrderId) REFERENCES dbo.Orders(Id)
            ) WITH (MEMORY_OPTIMIZED = ON);
            """);

        Assert.Equal(10778, exception.Number);
    }

    [Fact]
    public async Task ForeignKey_BetweenMemoryOptimizedTables_WithDeleteCascade_FailsToDeploy()
    {
        await AssertDeploySucceedsAsync(
            """
            CREATE TABLE dbo.Orders (Id INT NOT NULL PRIMARY KEY NONCLUSTERED) WITH (MEMORY_OPTIMIZED = ON);
            """);

        var exception = await AssertDeployFailsAsync(
            """
            CREATE TABLE dbo.Lines (
                Id INT NOT NULL PRIMARY KEY NONCLUSTERED,
                OrderId INT NOT NULL,
                CONSTRAINT FK_Lines_Orders FOREIGN KEY (OrderId) REFERENCES dbo.Orders(Id) ON DELETE CASCADE
            ) WITH (MEMORY_OPTIMIZED = ON);
            """);

        Assert.Equal(10794, exception.Number);
    }

    [Fact]
    public async Task ForeignKey_BetweenMemoryOptimizedTables_WithNoAction_DeploysCleanly()
    {
        await AssertDeploySucceedsAsync(
            """
            CREATE TABLE dbo.Orders (Id INT NOT NULL PRIMARY KEY NONCLUSTERED) WITH (MEMORY_OPTIMIZED = ON);
            CREATE TABLE dbo.Lines (
                Id INT NOT NULL PRIMARY KEY NONCLUSTERED,
                OrderId INT NOT NULL,
                CONSTRAINT FK_Lines_Orders FOREIGN KEY (OrderId) REFERENCES dbo.Orders(Id)
            ) WITH (MEMORY_OPTIMIZED = ON);
            """);
    }
}
