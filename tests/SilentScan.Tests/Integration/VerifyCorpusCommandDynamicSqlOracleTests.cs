using System.Text.Json;
using SilentScan.Verify;
using SilentScan.Verify.Commands;

namespace SilentScan.Tests.Integration;

/// <summary>
/// The whole reason this migration exists (task "migrate VerifyCorpusCommand off the file-parsed
/// catalog to live-catalog/sys.sql_modules"): before it, verify-corpus never deployed procedure
/// bodies at all, so a dynamic-SQL finding living inside one - the overwhelmingly common real
/// shape - could never be oracle-confirmed by verify-corpus no matter what. This proves the fix
/// end-to-end against the live Docker oracle, not a synthesized scenario: a proc body building
/// `EXEC(@sql)` from a literal must deploy, get read back from `sys.sql_modules`, get analyzed,
/// and get oracle-confirmed by `verify-corpus` itself.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class VerifyCorpusCommandDynamicSqlOracleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"silentscan-verify-dynamic-sql-test-{Guid.NewGuid():N}");
    private readonly string _manifestPath;

    public VerifyCorpusCommandDynamicSqlOracleTests()
    {
        Directory.CreateDirectory(_root);
        var cloneDir = Path.Combine(_root, "clones", "example");
        Directory.CreateDirectory(Path.Combine(cloneDir, "Tables"));
        Directory.CreateDirectory(Path.Combine(cloneDir, "Procedures"));

        File.WriteAllText(Path.Combine(cloneDir, "Tables", "orders.sql"), """
            CREATE TABLE dbo.Orders (Status VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, INDEX IX_Orders_Status (Status));
            GO
            """);

        // A fully literal EXEC(@sql) build - Tier A of CLAUDE.md's dynamic SQL policy, no
        // placeholder involved at all - so this must be findable and oracle-confirmable at the
        // DEFAULT (High) confidence threshold. Only reachable via procPaths, never ddlPaths -
        // proving procedure bodies now actually deploy, which they never did before this task.
        File.WriteAllText(Path.Combine(cloneDir, "Procedures", "usp_find_active.sql"), """
            CREATE PROCEDURE dbo.usp_FindActive AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM dbo.Orders WHERE Status = N''Active''';
                EXEC(@sql);
            END;
            GO
            """);

        _manifestPath = Path.Combine(_root, "manifest.json");
        File.WriteAllText(_manifestPath, """
            {
              "repos": [
                {
                  "name": "dynamic-sql-example",
                  "url": "https://github.com/example/example",
                  "commitSha": "abcdef0123456789abcdef0123456789abcdef01",
                  "license": "MIT",
                  "ddlPaths": ["Tables/*.sql"],
                  "procPaths": ["Procedures/*.sql"],
                  "declaredCollation": null
                }
              ]
            }
            """);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task RunAsync_ProcBodyDynamicSqlLiteral_DeploysAndOracleConfirmsScanForced()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await VerifyCorpusCommand.RunAsync(
            new VerifyCorpusCommand.VerifyCorpusOptions(_manifestPath, Path.Combine(_root, "clones"), RepoFilter: null, "high"),
            SqlServerOptions.LocalDocker, stdout, stderr, CancellationToken.None);

        using var document = JsonDocument.Parse(stdout.ToString());
        var summary = document.RootElement.GetProperty("dynamic-sql-example");

        var confirmed = summary.GetProperty("Confirmed");
        Assert.True(confirmed.GetArrayLength() > 0, $"Expected at least one Confirmed finding. Full output:\n{stdout}");

        var finding = confirmed[0].GetProperty("Finding");
        Assert.Equal("ScanForced", finding.GetProperty("Verdict").GetString());
        Assert.NotEqual(JsonValueKind.Null, finding.GetProperty("DynamicSqlCallSite").ValueKind);

        var dynamicSqlSummary = summary.GetProperty("DynamicSql");
        Assert.True(dynamicSqlSummary.GetProperty("AnalyzedCount").GetInt32() > 0);

        Assert.Equal(0, exitCode);
    }
}
