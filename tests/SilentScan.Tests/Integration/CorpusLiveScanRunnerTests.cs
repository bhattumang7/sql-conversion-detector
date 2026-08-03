using SilentScan.Core.Corpus;
using SilentScan.Core.Rules;
using SilentScan.Live.Corpus;
using SilentScan.Verify;

namespace SilentScan.Tests.Integration;

/// <summary>
/// End-to-end proof of the roadmap item "make the corpus catalog engine-authoritative: deploy
/// DDL to Docker, read schema back" - <see cref="CorpusLiveScanRunner"/> deploys a small
/// synthetic repo's own files (not a manifest fixture string, real files on disk, matching how
/// a real corpus repo's clone looks) to a disposable database, reads the catalog and module text
/// back from the engine, and reports findings that map back to those same real files. Unlike
/// <see cref="LiveScanRunnerTests"/> (a pre-existing live database, no deployment step at all),
/// this specifically exercises the deployment side: the stub-then-ALTER pattern real corpora
/// use (First Responder Kit's every sp_Blitz*.sql file) and the CREATE-OR-ALTER rewrite
/// (<c>ScriptDeployer.RewriteAlterToCreateOrAlter</c>) that makes it deployable without ever
/// running the stub's own dynamic SQL.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class CorpusLiveScanRunnerTests : IDisposable
{
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("silentscan-corpus-live-").FullName;

    public void Dispose() => Directory.Delete(_repoRoot, recursive: true);

    private static CorpusRepoEntry BuildRepo(string name) => new(
        Name: name,
        Url: "https://example.invalid/" + name,
        CommitSha: new string('0', 40),
        License: "MIT",
        DdlPaths: ["Tables/*.sql"],
        ProcPaths: ["Procedures/*.sql"],
        DeclaredCollation: "SQL_Latin1_General_CP1_CI_AS",
        Notes: null);

    private void WriteFile(string relativePath, string contents)
    {
        var fullPath = Path.Combine(_repoRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents);
    }

    [Fact]
    public async Task RunAsync_StubThenAlterProcedurePattern_DeploysAndResolvesToBaseColumn()
    {
        WriteFile("Tables/Orders.sql", """
            CREATE TABLE dbo.Orders (
                OrderId INT NOT NULL PRIMARY KEY,
                OrderCode varchar(30) NOT NULL,
                INDEX IX_OrderCode (OrderCode));
            """);

        // The real-world stub-then-ALTER pattern (First Responder Kit's every sp_Blitz*.sql
        // file): the stub's own EXEC(...) is never executed (whitelist), so without the
        // CREATE-OR-ALTER rewrite this ALTER would fail with "Invalid object name" - the object
        // genuinely never got created.
        WriteFile("Procedures/usp_FindOrder.sql", """
            IF OBJECT_ID('dbo.usp_FindOrder') IS NULL EXEC('CREATE PROCEDURE dbo.usp_FindOrder AS RETURN 0');
            GO
            ALTER PROCEDURE dbo.usp_FindOrder @OrderCode NVARCHAR(30) AS
            BEGIN
                SELECT OrderId FROM dbo.Orders WHERE OrderCode = @OrderCode;
            END
            """);

        var repo = BuildRepo(nameof(RunAsync_StubThenAlterProcedurePattern_DeploysAndResolvesToBaseColumn));
        var result = await CorpusLiveScanRunner.RunAsync(repo, _repoRoot, SqlServerOptions.LocalDocker);

        Assert.Empty(result.UnmappedModules);
        Assert.Equal(1, result.ModulesAnalyzed);
        Assert.Empty(result.UnanalyzableModules);

        var finding = Assert.Single(result.Report.TypedFindings, f => f.Column.ColumnName == "OrderCode");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal("dbo.Orders", finding.Column.TableQualifiedName);
        Assert.True(finding.Column.Indexed);

        // The whole point of this roadmap item: the finding's source path is the REAL repo
        // file, not a synthetic "database:object" placeholder - CLAUDE.md: "Corpus findings
        // still map back to the defining repo file, since the study cites repos."
        Assert.Equal(Path.Combine(_repoRoot, "Procedures", "usp_FindOrder.sql"), finding.SourcePath);
    }

    [Fact]
    public async Task RunAsync_TempTableInsideProcedureBody_ResolvedAsCatalogExtra()
    {
        // Engine metadata alone knows nothing about a temp table declared inside a proc body -
        // only MergeFileModeExtras (built from the POST-DEPLOYMENT module text, exactly like
        // LiveScanRunner's own pattern) can resolve it.
        WriteFile("Procedures/usp_BuildReport.sql", """
            CREATE PROCEDURE dbo.usp_BuildReport AS
            BEGIN
                CREATE TABLE #Report (Code varchar(20) NOT NULL);
                INSERT INTO #Report (Code) VALUES ('X');
                SELECT Code FROM #Report WHERE Code = N'X';
            END
            """);

        var repo = BuildRepo(nameof(RunAsync_TempTableInsideProcedureBody_ResolvedAsCatalogExtra)) with { DdlPaths = [] };
        var result = await CorpusLiveScanRunner.RunAsync(repo, _repoRoot, SqlServerOptions.LocalDocker);

        Assert.Empty(result.UnmappedModules);
        var finding = Assert.Single(result.Report.TypedFindings, f => f.Column.ColumnName == "Code");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.Equal("#Report", finding.Column.TableQualifiedName);
    }

    [Fact]
    public async Task RunAsync_DisallowedStatementInDdlFile_SkippedAndReportedNeverExecuted()
    {
        WriteFile("Tables/Orders.sql", """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY);
            GO
            INSERT INTO dbo.Orders (OrderId) VALUES (1);
            """);

        var repo = BuildRepo(nameof(RunAsync_DisallowedStatementInDdlFile_SkippedAndReportedNeverExecuted)) with { ProcPaths = [] };
        var result = await CorpusLiveScanRunner.RunAsync(repo, _repoRoot, SqlServerOptions.LocalDocker);

        Assert.Contains(result.DeploymentMessages, m => m.Contains("InsertStatement", StringComparison.Ordinal));
        Assert.Equal(1, result.CatalogSummary.TableCount);
    }
}
