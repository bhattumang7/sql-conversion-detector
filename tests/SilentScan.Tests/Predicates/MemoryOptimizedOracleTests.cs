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

    [Theory]
    [InlineData("HIERARCHYID")]
    [InlineData("GEOMETRY")]
    [InlineData("GEOGRAPHY")]
    public async Task SpatialOrHierarchyIdColumn_OnMemoryOptimizedTable_FailsToDeploy(string type)
    {
        var exception = await AssertDeployFailsAsync(
            $"""
            CREATE TABLE dbo.SpatialWidgets (Id INT NOT NULL PRIMARY KEY NONCLUSTERED, Tag {type} NULL) WITH (MEMORY_OPTIMIZED = ON);
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
    public async Task FilteredIndex_OnMemoryOptimizedTable_FailsToDeploy()
    {
        await AssertDeploySucceedsAsync(
            """
            CREATE TABLE dbo.Widgets (Id INT NOT NULL PRIMARY KEY NONCLUSTERED, Amount INT NULL) WITH (MEMORY_OPTIMIZED = ON);
            """);

        var exception = await AssertDeployFailsAsync(
            """
            CREATE INDEX IX_Amount ON dbo.Widgets (Amount) WHERE Amount IS NOT NULL;
            """);

        Assert.Equal(10794, exception.Number);
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

    [Theory]
    [InlineData("VARCHAR(50)")]
    [InlineData("CHAR(10)")]
    public async Task Utf8Collation_OnMemoryOptimizedTableColumn_FailsToDeploy(string type)
    {
        var exception = await AssertDeployFailsAsync(
            $"""
            CREATE TABLE dbo.Widgets (Id INT NOT NULL PRIMARY KEY NONCLUSTERED, Tag {type} COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL) WITH (MEMORY_OPTIMIZED = ON);
            """);

        Assert.Equal(12356, exception.Number);
    }

    [Fact]
    public async Task Utf8Collation_OnOrdinaryTableColumn_DeploysCleanly()
    {
        await AssertDeploySucceedsAsync(
            """
            CREATE TABLE dbo.Widgets (Id INT NOT NULL PRIMARY KEY, Tag VARCHAR(50) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL);
            """);
    }

    [Fact]
    public async Task Utf8Collation_OnNvarcharMemoryOptimizedColumn_DeploysCleanly()
    {
        await AssertDeploySucceedsAsync(
            """
            CREATE TABLE dbo.Widgets (Id INT NOT NULL PRIMARY KEY NONCLUSTERED, Tag NVARCHAR(50) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL) WITH (MEMORY_OPTIMIZED = ON);
            """);
    }

    [Theory]
    [InlineData("UPPER(N'a')")]
    [InlineData("LOWER(N'a')")]
    [InlineData("REPLACE(N'a', N'a', N'b')")]
    [InlineData("CHARINDEX(N'a', N'abc')")]
    [InlineData("STUFF(N'abc', 1, 1, N'x')")]
    [InlineData("REVERSE(N'abc')")]
    [InlineData("PATINDEX(N'%a%', N'abc')")]
    [InlineData("QUOTENAME(N'a')")]
    [InlineData("DATALENGTH(N'abc')")]
    [InlineData("ISNUMERIC(N'1')")]
    [InlineData("ISDATE(N'2020-01-01')")]
    [InlineData("HASHBYTES('SHA2_256', N'a')")]
    [InlineData("CONCAT(N'a', N'b')")]
    [InlineData("FORMAT(SYSDATETIME(), N'yyyy')")]
    [InlineData("SOUNDEX(N'a')")]
    [InlineData("LEFT(N'abc', 1)")]
    [InlineData("RIGHT(N'abc', 1)")]
    public async Task DenylistedBuiltin_InNativelyCompiledProcedure_FailsToDeploy(string expression)
    {
        var exception = await AssertDeployFailsAsync(
            $"""
            CREATE PROCEDURE dbo.NativeDenylistProbe
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @v NVARCHAR(50) = {expression};
            END;
            """);

        Assert.Equal(10794, exception.Number);
    }

    [Fact]
    public async Task AllowlistedBuiltin_InNativelyCompiledProcedure_DeploysCleanly()
    {
        await AssertDeploySucceedsAsync(
            """
            CREATE PROCEDURE dbo.NativeAllowlistProbe
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @a FLOAT = ABS(-1.0);
                DECLARE @b DATETIME2 = DATEADD(DAY, 1, SYSDATETIME());
                DECLARE @c INT = LEN(N'abc');
                DECLARE @d UNIQUEIDENTIFIER = NEWID();
                DECLARE @e INT = ISNULL(NULL, 1);
            END;
            """);
    }

    [Fact]
    public async Task DenylistedBuiltin_InOrdinaryInterpretedProcedure_DeploysCleanly()
    {
        await AssertDeploySucceedsAsync(
            """
            CREATE PROCEDURE dbo.InterpretedProbe
            AS
            BEGIN
                DECLARE @v NVARCHAR(20) = UPPER(N'a');
            END;
            """);
    }

    [Theory]
    [InlineData("ERROR_NUMBER()", "INT")]
    [InlineData("ERROR_MESSAGE()", "NVARCHAR(2048)")]
    [InlineData("ERROR_SEVERITY()", "INT")]
    [InlineData("ERROR_STATE()", "INT")]
    [InlineData("ERROR_LINE()", "INT")]
    [InlineData("ERROR_PROCEDURE()", "NVARCHAR(126)")]
    public async Task ErrorFunction_OutsideCatchBlock_FailsToDeploy(string expression, string declaredType)
    {
        var exception = await AssertDeployFailsAsync(
            $"""
            CREATE PROCEDURE dbo.NativeErrorOutsideCatchProbe
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @v {declaredType} = {expression};
            END;
            """);

        Assert.Equal(10792, exception.Number);
    }

    [Fact]
    public async Task ErrorNumber_InsideCatchBlock_DeploysCleanly()
    {
        await AssertDeploySucceedsAsync(
            """
            CREATE PROCEDURE dbo.NativeErrorInsideCatchProbe
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @lastError INT, @x INT = 0;
                BEGIN TRY
                    SET @lastError = 1 / @x;
                END TRY
                BEGIN CATCH
                    SET @lastError = ERROR_NUMBER();
                END CATCH
            END;
            """);
    }

    [Fact]
    public async Task ErrorNumber_OutsideCatchBlock_InOrdinaryInterpretedProcedure_DeploysCleanly()
    {
        await AssertDeploySucceedsAsync(
            """
            CREATE PROCEDURE dbo.InterpretedErrorProbe
            AS
            BEGIN
                DECLARE @lastError INT = ERROR_NUMBER();
            END;
            """);
    }

    [Fact]
    public async Task ExecInterpretedProcedure_FromNativelyCompiledProcedure_FailsToDeploy()
    {
        await AssertDeploySucceedsAsync(
            """
            CREATE TABLE dbo.CalleeAuditLog (Id INT NOT NULL IDENTITY PRIMARY KEY, Message NVARCHAR(200) NOT NULL);
            """);
        await AssertDeploySucceedsAsync(
            """
            CREATE PROCEDURE dbo.LogCalleeAudit
            AS
            BEGIN
                INSERT INTO dbo.CalleeAuditLog (Message) VALUES (N'audited');
            END;
            """);

        var exception = await AssertDeployFailsAsync(
            """
            CREATE PROCEDURE dbo.NativeExecInterpretedProbe
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                EXEC dbo.LogCalleeAudit;
            END;
            """);

        Assert.Equal(12342, exception.Number);
    }

    [Fact]
    public async Task CallInterpretedScalarFunction_FromNativelyCompiledProcedure_FailsToDeploy()
    {
        await AssertDeploySucceedsAsync(
            """
            CREATE FUNCTION dbo.ComputeCalleeTax(@amount DECIMAL(19,4))
            RETURNS DECIMAL(19,4)
            AS
            BEGIN
                RETURN @amount * 0.1;
            END;
            """);

        var exception = await AssertDeployFailsAsync(
            """
            CREATE PROCEDURE dbo.NativeCallInterpretedFunctionProbe
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @tax DECIMAL(19,4) = dbo.ComputeCalleeTax(100);
            END;
            """);

        Assert.Equal(12344, exception.Number);
    }

    [Fact]
    public async Task ExecNativelyCompiledProcedure_FromNativelyCompiledProcedure_DeploysCleanly()
    {
        await AssertDeploySucceedsAsync(
            """
            CREATE PROCEDURE dbo.NativeCalleeProbe
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @dummy INT = 1;
            END;
            """);

        await AssertDeploySucceedsAsync(
            """
            CREATE PROCEDURE dbo.NativeCallerProbe
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                EXEC dbo.NativeCalleeProbe;
            END;
            """);
    }

    [Fact]
    public async Task ExecInterpretedProcedure_FromOrdinaryInterpretedProcedure_DeploysCleanly()
    {
        await AssertDeploySucceedsAsync(
            """
            CREATE PROCEDURE dbo.InterpretedCalleeProbe
            AS
            BEGIN
                SELECT 1;
            END;
            """);

        await AssertDeploySucceedsAsync(
            """
            CREATE PROCEDURE dbo.InterpretedCallerProbe
            AS
            BEGIN
                EXEC dbo.InterpretedCalleeProbe;
            END;
            """);
    }

    [Fact]
    public async Task MemoryOptimizedAndLedger_BothOn_FailsToDeploy()
    {
        var exception = await AssertDeployFailsAsync(
            """
            CREATE TABLE dbo.LedgerConflictProbe (Id INT NOT NULL PRIMARY KEY NONCLUSTERED) WITH (MEMORY_OPTIMIZED = ON, LEDGER = ON);
            """);

        Assert.Equal(12359, exception.Number);
    }

    [Fact]
    public async Task MemoryOptimizedOnly_DeploysCleanly()
    {
        await AssertDeploySucceedsAsync(
            """
            CREATE TABLE dbo.MemoryOptimizedOnlyProbe (Id INT NOT NULL PRIMARY KEY NONCLUSTERED) WITH (MEMORY_OPTIMIZED = ON);
            """);
    }
}
