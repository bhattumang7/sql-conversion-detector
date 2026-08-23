using System.Text.Json;
using SilentScan.Verify;
using SilentScan.Verify.Commands;

namespace SilentScan.Tests.Integration;

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

[Trait("Category", "Oracle")]
public sealed class VerifyCorpusCommandConfirmedUnindexedExitCodeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"silentscan-verify-unindexed-exitcode-test-{Guid.NewGuid():N}");
    private readonly string _manifestPath;

    public VerifyCorpusCommandConfirmedUnindexedExitCodeTests()
    {
        Directory.CreateDirectory(_root);
        var cloneDir = Path.Combine(_root, "clones", "example");
        Directory.CreateDirectory(cloneDir);

        File.WriteAllText(Path.Combine(cloneDir, "schema.sql"), """
            CREATE TABLE dbo.Orders (Status VARCHAR(MAX) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            GO
            CREATE VIEW dbo.vw_ActiveOrders AS SELECT Status FROM dbo.Orders WHERE Status = N'Active';
            GO
            """);

        _manifestPath = Path.Combine(_root, "manifest.json");
        File.WriteAllText(_manifestPath, """
            {
              "repos": [
                {
                  "name": "unindexed-example",
                  "url": "https://github.com/example/example",
                  "commitSha": "abcdef0123456789abcdef0123456789abcdef01",
                  "license": "MIT",
                  "ddlPaths": ["*.sql"],
                  "declaredCollation": null
                }
              ]
            }
            """);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task RunAsync_OnlyProbeWorthyFindingIsConfirmedUnindexed_ExitsZero()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await VerifyCorpusCommand.RunAsync(
            new VerifyCorpusCommand.VerifyCorpusOptions(_manifestPath, Path.Combine(_root, "clones"), RepoFilter: null, "high"),
            SqlServerOptions.LocalDocker, stdout, stderr, CancellationToken.None);

        using var document = JsonDocument.Parse(stdout.ToString());
        var summary = document.RootElement.GetProperty("unindexed-example");

        Assert.True(summary.GetProperty("ProbeWorthyFindingCount").GetInt32() > 0);
        Assert.Empty(summary.GetProperty("Confirmed").EnumerateArray());
        Assert.True(summary.GetProperty("ConfirmedUnindexed").GetArrayLength() > 0, $"Expected at least one ConfirmedUnindexed finding. Full output:\n{stdout}");

        Assert.Equal(0, exitCode);
    }
}
