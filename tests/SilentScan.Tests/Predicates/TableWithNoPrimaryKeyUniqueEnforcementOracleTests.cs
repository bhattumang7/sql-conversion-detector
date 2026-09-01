using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class TableWithNoPrimaryKeyUniqueEnforcementOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(TableWithNoPrimaryKeyUniqueEnforcementOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.UniqueConstraintNoPk (Id INT NOT NULL UNIQUE);
        GO
        CREATE TABLE dbo.UniqueIndexNoPk (Id INT NOT NULL);
        GO
        CREATE UNIQUE INDEX IX_UniqueIndexNoPk_Id ON dbo.UniqueIndexNoPk (Id);
        GO
        CREATE TABLE dbo.FilteredUniqueIndexNoPk (Id INT NULL);
        GO
        CREATE UNIQUE INDEX IX_FilteredUniqueIndexNoPk_Id ON dbo.FilteredUniqueIndexNoPk (Id) WHERE Id IS NOT NULL;
        GO
        CREATE TABLE dbo.DisabledUniqueIndexNoPk (Id INT NOT NULL);
        GO
        CREATE UNIQUE INDEX IX_DisabledUniqueIndexNoPk_Id ON dbo.DisabledUniqueIndexNoPk (Id);
        GO
        ALTER INDEX IX_DisabledUniqueIndexNoPk_Id ON dbo.DisabledUniqueIndexNoPk DISABLE;
        GO
        CREATE TABLE dbo.NoConstraintAtAllNoPk (Id INT NOT NULL);
        GO
        """;

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        return connection;
    }

    [Fact]
    public async Task UniqueConstraintNoPrimaryKey_RejectsDuplicateInsert()
    {
        await using var connection = await OpenConnectionAsync();

        await using (var seed = new SqlCommand("INSERT INTO dbo.UniqueConstraintNoPk (Id) VALUES (1);", connection))
        {
            await seed.ExecuteNonQueryAsync();
        }

        await using var duplicate = new SqlCommand("INSERT INTO dbo.UniqueConstraintNoPk (Id) VALUES (1);", connection);
        var exception = await Assert.ThrowsAsync<SqlException>(() => duplicate.ExecuteNonQueryAsync());

        Assert.Contains("Violation of UNIQUE KEY constraint", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UniqueIndexNoPrimaryKey_RejectsDuplicateInsert()
    {
        await using var connection = await OpenConnectionAsync();

        await using (var seed = new SqlCommand("INSERT INTO dbo.UniqueIndexNoPk (Id) VALUES (1);", connection))
        {
            await seed.ExecuteNonQueryAsync();
        }

        await using var duplicate = new SqlCommand("INSERT INTO dbo.UniqueIndexNoPk (Id) VALUES (1);", connection);
        var exception = await Assert.ThrowsAsync<SqlException>(() => duplicate.ExecuteNonQueryAsync());

        Assert.Contains("Cannot insert duplicate key row", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FilteredUniqueIndexNoPrimaryKey_AcceptsDuplicateOutsideFilter()
    {
        await using var connection = await OpenConnectionAsync();

        await using (var seed = new SqlCommand("INSERT INTO dbo.FilteredUniqueIndexNoPk (Id) VALUES (NULL);", connection))
        {
            await seed.ExecuteNonQueryAsync();
        }

        await using var duplicate = new SqlCommand("INSERT INTO dbo.FilteredUniqueIndexNoPk (Id) VALUES (NULL);", connection);
        await duplicate.ExecuteNonQueryAsync();

        await using var count = new SqlCommand("SELECT COUNT(*) FROM dbo.FilteredUniqueIndexNoPk WHERE Id IS NULL;", connection);
        Assert.Equal(2, (int)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task DisabledUniqueIndexNoPrimaryKey_AcceptsDuplicateInsert()
    {
        await using var connection = await OpenConnectionAsync();

        await using (var seed = new SqlCommand("INSERT INTO dbo.DisabledUniqueIndexNoPk (Id) VALUES (1);", connection))
        {
            await seed.ExecuteNonQueryAsync();
        }

        await using var duplicate = new SqlCommand("INSERT INTO dbo.DisabledUniqueIndexNoPk (Id) VALUES (1);", connection);
        await duplicate.ExecuteNonQueryAsync();

        await using var count = new SqlCommand("SELECT COUNT(*) FROM dbo.DisabledUniqueIndexNoPk WHERE Id = 1;", connection);
        Assert.Equal(2, (int)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task NoConstraintAtAllNoPrimaryKey_AcceptsByteForByteIdenticalRows()
    {
        await using var connection = await OpenConnectionAsync();

        await using (var seed = new SqlCommand("INSERT INTO dbo.NoConstraintAtAllNoPk (Id) VALUES (1);", connection))
        {
            await seed.ExecuteNonQueryAsync();
        }

        await using var duplicate = new SqlCommand("INSERT INTO dbo.NoConstraintAtAllNoPk (Id) VALUES (1);", connection);
        await duplicate.ExecuteNonQueryAsync();

        await using var count = new SqlCommand("SELECT COUNT(*) FROM dbo.NoConstraintAtAllNoPk WHERE Id = 1;", connection);
        Assert.Equal(2, (int)(await count.ExecuteScalarAsync())!);
    }
}
