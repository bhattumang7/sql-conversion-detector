using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;
using SilentScan.Verify.Catalog;
using SilentScan.Verify.Deployment;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class CrossModuleLockOrderScannerTransactionScopeOracleTests : OracleTestFixture
{
    private static readonly string[] SingleT1 = ["T1"];
    private static readonly string[] SingleT2 = ["T2"];
    private static readonly string[] BothTables = ["T1", "T2"];

    protected override string DatabaseNameSeed => nameof(CrossModuleLockOrderScannerTransactionScopeOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.T1 (Id INT NOT NULL PRIMARY KEY);
        GO
        CREATE TABLE dbo.T2 (Id INT NOT NULL PRIMARY KEY);
        GO
        INSERT INTO dbo.T1 VALUES (1);
        INSERT INTO dbo.T2 VALUES (1);
        """;

    [Fact]
    public async Task CommittedTransaction_ReleasesItsLockBeforeALaterTransactionReacquiresIt()
    {
        await using var work = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await work.OpenAsync();
        var sessionId = (int)(short)(await new SqlCommand("SELECT @@SPID;", work).ExecuteScalarAsync())!;

        await new SqlCommand("BEGIN TRAN; UPDATE dbo.T1 SET Id = Id; COMMIT;", work).ExecuteNonQueryAsync();
        await new SqlCommand("BEGIN TRAN; UPDATE dbo.T2 SET Id = Id;", work).ExecuteNonQueryAsync();

        Assert.Equal(SingleT2, await ReadIntentExclusiveLockedTableNamesAsync(sessionId));

        await new SqlCommand("UPDATE dbo.T1 SET Id = Id;", work).ExecuteNonQueryAsync();

        Assert.Equal(
            BothTables,
            (await ReadIntentExclusiveLockedTableNamesAsync(sessionId)).OrderBy(n => n, StringComparer.Ordinal));

        await new SqlCommand("COMMIT;", work).ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task RollbackToSavepoint_ReleasesOnlyTheLocksAcquiredAfterTheSavepoint()
    {
        await using var work = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await work.OpenAsync();
        var sessionId = (int)(short)(await new SqlCommand("SELECT @@SPID;", work).ExecuteScalarAsync())!;

        await new SqlCommand(
            "BEGIN TRAN; UPDATE dbo.T1 SET Id = Id; SAVE TRAN sp1; UPDATE dbo.T2 SET Id = Id;", work).ExecuteNonQueryAsync();

        Assert.Equal(
            BothTables,
            (await ReadIntentExclusiveLockedTableNamesAsync(sessionId)).OrderBy(n => n, StringComparer.Ordinal));

        await new SqlCommand("ROLLBACK TRAN sp1;", work).ExecuteNonQueryAsync();

        Assert.Equal(SingleT1, await ReadIntentExclusiveLockedTableNamesAsync(sessionId));

        await new SqlCommand("COMMIT;", work).ExecuteNonQueryAsync();
    }

    private async Task<IReadOnlyList<string>> ReadIntentExclusiveLockedTableNamesAsync(int sessionId)
    {
        await using var observer = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await observer.OpenAsync();

        await using var command = new SqlCommand(
            """
            SELECT o.name
            FROM sys.dm_tran_locks l
            JOIN sys.objects o ON l.resource_associated_entity_id = o.object_id
            WHERE l.request_session_id = @sessionId AND l.resource_type = 'OBJECT' AND l.request_mode LIKE 'IX%';
            """,
            observer);
        command.Parameters.AddWithValue("@sessionId", sessionId);

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private async Task<IReadOnlyList<CrossModuleLockOrderFinding>> ScanDeployedProceduresAsync(string proceduresSql)
    {
        var connectionString = Options.BuildConnectionString(DatabaseName);
        await new ScriptDeployer(Options).DeployAsync(proceduresSql, DatabaseName);

        var liveCatalog = await new LiveCatalogReader(connectionString).ReadAsync();
        var moduleResult = await new LiveModuleReader(connectionString).ReadAsync();

        var parseResults = moduleResult.Modules
            .Select(m => SqlScriptParser.ParseText(m.QualifiedName, m.Definition, m.UsesQuotedIdentifier, liveCatalog.CompatibilityLevel))
            .ToList();

        var catalog = CatalogBuilder.Build(
            parseResults, liveCatalog.DefaultCollation?.Name, liveCatalog.TempdbCollation?.Name, liveCatalog.IsAnsiNullDefaultOn,
            knownTables: liveCatalog.Tables.Where(t => t.Kind == CatalogTableKind.Table).ToList());

        return CrossModuleLockOrderScanner.Scan(parseResults, catalog);
    }

    [Fact]
    public async Task TwoSequentialCommittedTransactionsInOneProcedure_NeverFireAgainstASiblingWithTheSameRealLockOrder()
    {
        var findings = await ScanDeployedProceduresAsync("""
            CREATE PROCEDURE dbo.ProcA AS
            BEGIN
                BEGIN TRANSACTION;
                UPDATE dbo.T1 SET Id = Id;
                COMMIT TRANSACTION;
                BEGIN TRANSACTION;
                UPDATE dbo.T2 SET Id = Id;
                UPDATE dbo.T1 SET Id = Id;
                COMMIT TRANSACTION;
            END;
            GO
            CREATE PROCEDURE dbo.ProcB AS
            BEGIN
                BEGIN TRANSACTION;
                UPDATE dbo.T2 SET Id = Id;
                UPDATE dbo.T1 SET Id = Id;
                COMMIT TRANSACTION;
            END;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task NestedBeginTransaction_DoesNotSplitTheOuterTransactionsLockScope()
    {
        var findings = await ScanDeployedProceduresAsync("""
            CREATE PROCEDURE dbo.ProcA AS
            BEGIN
                BEGIN TRANSACTION;
                BEGIN TRANSACTION;
                UPDATE dbo.T2 SET Id = Id;
                COMMIT TRANSACTION;
                UPDATE dbo.T1 SET Id = Id;
                COMMIT TRANSACTION;
            END;
            GO
            CREATE PROCEDURE dbo.ProcB AS
            BEGIN
                BEGIN TRANSACTION;
                UPDATE dbo.T1 SET Id = Id;
                UPDATE dbo.T2 SET Id = Id;
                COMMIT TRANSACTION;
            END;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.T1", finding.FirstTableQualifiedName);
        Assert.Equal("dbo.T2", finding.SecondTableQualifiedName);
        Assert.Equal("dbo.ProcB", finding.FirstTableFirstOrdering.ProcedureQualifiedName);
        Assert.Equal("dbo.ProcA", finding.SecondTableFirstOrdering.ProcedureQualifiedName);
    }

    [Fact]
    public async Task RollbackToSavepoint_DoesNotSplitTheTransactionsLockScope_ReleasedWriteIsExcludedFromTheOrdering()
    {
        var findings = await ScanDeployedProceduresAsync("""
            CREATE PROCEDURE dbo.ProcA AS
            BEGIN
                BEGIN TRANSACTION;
                SAVE TRANSACTION sp1;
                UPDATE dbo.T2 SET Id = Id;
                ROLLBACK TRANSACTION sp1;
                UPDATE dbo.T1 SET Id = Id;
                UPDATE dbo.T2 SET Id = Id;
                COMMIT TRANSACTION;
            END;
            GO
            CREATE PROCEDURE dbo.ProcB AS
            BEGIN
                BEGIN TRANSACTION;
                UPDATE dbo.T2 SET Id = Id;
                UPDATE dbo.T1 SET Id = Id;
                COMMIT TRANSACTION;
            END;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.T1", finding.FirstTableQualifiedName);
        Assert.Equal("dbo.T2", finding.SecondTableQualifiedName);
        Assert.Equal("dbo.ProcA", finding.FirstTableFirstOrdering.ProcedureQualifiedName);
        Assert.Equal("dbo.ProcB", finding.SecondTableFirstOrdering.ProcedureQualifiedName);
    }

    [Fact]
    public async Task UnnamedRollbackEndsTheTransaction_LaterTransactionInTheSameProcedureIsComparedOnItsOwn()
    {
        var findings = await ScanDeployedProceduresAsync("""
            CREATE PROCEDURE dbo.ProcA AS
            BEGIN
                BEGIN TRANSACTION;
                UPDATE dbo.T1 SET Id = Id;
                ROLLBACK TRANSACTION;
                BEGIN TRANSACTION;
                UPDATE dbo.T2 SET Id = Id;
                UPDATE dbo.T1 SET Id = Id;
                COMMIT TRANSACTION;
            END;
            GO
            CREATE PROCEDURE dbo.ProcB AS
            BEGIN
                BEGIN TRANSACTION;
                UPDATE dbo.T1 SET Id = Id;
                UPDATE dbo.T2 SET Id = Id;
                COMMIT TRANSACTION;
            END;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.T1", finding.FirstTableQualifiedName);
        Assert.Equal("dbo.T2", finding.SecondTableQualifiedName);
        Assert.Equal("dbo.ProcB", finding.FirstTableFirstOrdering.ProcedureQualifiedName);
        Assert.Equal("dbo.ProcA", finding.SecondTableFirstOrdering.ProcedureQualifiedName);
    }
}
