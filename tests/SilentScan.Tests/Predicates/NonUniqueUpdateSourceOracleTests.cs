using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class NonUniqueUpdateSourceOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(NonUniqueUpdateSourceOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.TargetT (Id INT NOT NULL PRIMARY KEY, Val INT NULL);
        GO
        CREATE TABLE dbo.SourceNonUnique (TargetId INT NOT NULL, Val INT NOT NULL);
        GO
        CREATE TABLE dbo.SourceUnique (TargetId INT NOT NULL, Val INT NOT NULL);
        GO
        CREATE UNIQUE INDEX UX_SourceUnique_TargetId ON dbo.SourceUnique(TargetId);
        GO
        CREATE TABLE dbo.SourceCompositeUnique (TargetId INT NOT NULL, Cat INT NOT NULL, Val INT NOT NULL);
        GO
        ALTER TABLE dbo.SourceCompositeUnique ADD CONSTRAINT UX_Composite UNIQUE (TargetId, Cat);
        GO
        """;

    private static readonly int[] PossibleNonUniqueResults = [100, 200, 300];

    private static readonly int[] PossibleCompositeSubsetResults = [111, 222];

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        return connection;
    }

    [Fact]
    public async Task NonUniqueSource_MultipleMatchingRows_SilentlyPicksOneOfThem()
    {
        await using var connection = await OpenConnectionAsync();

        await using (var seed = new SqlCommand(
            """
            INSERT INTO dbo.TargetT (Id, Val) VALUES (1, NULL);
            INSERT INTO dbo.SourceNonUnique (TargetId, Val) VALUES (1, 100), (1, 200), (1, 300);
            """, connection))
        {
            await seed.ExecuteNonQueryAsync();
        }

        await using (var update = new SqlCommand(
            "UPDATE t SET t.Val = s.Val FROM dbo.TargetT t JOIN dbo.SourceNonUnique s ON t.Id = s.TargetId;", connection))
        {
            await update.ExecuteNonQueryAsync();
        }

        await using var read = new SqlCommand("SELECT Val FROM dbo.TargetT WHERE Id = 1;", connection);
        var result = (int)(await read.ExecuteScalarAsync())!;

        Assert.Contains(result, PossibleNonUniqueResults);
    }

    [Fact]
    public async Task UniqueSource_SingleMatchingRow_IsDeterministic()
    {
        await using var connection = await OpenConnectionAsync();

        await using (var seed = new SqlCommand(
            """
            INSERT INTO dbo.TargetT (Id, Val) VALUES (1, NULL);
            INSERT INTO dbo.SourceUnique (TargetId, Val) VALUES (1, 999);
            """, connection))
        {
            await seed.ExecuteNonQueryAsync();
        }

        await using (var update = new SqlCommand(
            "UPDATE t SET t.Val = s.Val FROM dbo.TargetT t JOIN dbo.SourceUnique s ON t.Id = s.TargetId;", connection))
        {
            await update.ExecuteNonQueryAsync();
        }

        await using var read = new SqlCommand("SELECT Val FROM dbo.TargetT WHERE Id = 1;", connection);
        var result = (int)(await read.ExecuteScalarAsync())!;

        Assert.Equal(999, result);
    }

    [Fact]
    public async Task CompositeUniqueSuperset_JoinOnSubsetOnly_StillMultiMatches()
    {
        await using var connection = await OpenConnectionAsync();

        await using (var seed = new SqlCommand(
            """
            INSERT INTO dbo.TargetT (Id, Val) VALUES (1, NULL);
            INSERT INTO dbo.SourceCompositeUnique (TargetId, Cat, Val) VALUES (1, 10, 111), (1, 20, 222);
            """, connection))
        {
            await seed.ExecuteNonQueryAsync();
        }

        await using (var update = new SqlCommand(
            "UPDATE t SET t.Val = s.Val FROM dbo.TargetT t JOIN dbo.SourceCompositeUnique s ON t.Id = s.TargetId;", connection))
        {
            await update.ExecuteNonQueryAsync();
        }

        await using var read = new SqlCommand("SELECT Val FROM dbo.TargetT WHERE Id = 1;", connection);
        var result = (int)(await read.ExecuteScalarAsync())!;

        Assert.Contains(result, PossibleCompositeSubsetResults);
    }

    [Fact]
    public async Task Merge_SameNonUniqueSource_RaisesAnErrorInsteadOfPickingSilently()
    {
        await using var connection = await OpenConnectionAsync();

        await using (var seed = new SqlCommand(
            """
            INSERT INTO dbo.TargetT (Id, Val) VALUES (1, NULL);
            INSERT INTO dbo.SourceNonUnique (TargetId, Val) VALUES (1, 100), (1, 200);
            """, connection))
        {
            await seed.ExecuteNonQueryAsync();
        }

        await using var merge = new SqlCommand(
            """
            MERGE dbo.TargetT AS t USING dbo.SourceNonUnique AS s ON t.Id = s.TargetId
            WHEN MATCHED THEN UPDATE SET t.Val = s.Val;
            """, connection);

        var exception = await Assert.ThrowsAsync<SqlException>(() => merge.ExecuteNonQueryAsync());
        Assert.Equal(8672, exception.Number);
        Assert.Contains("attempted to UPDATE or DELETE the same row more than once", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
