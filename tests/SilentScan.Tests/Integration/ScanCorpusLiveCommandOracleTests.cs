using System.Text.Json;
using SilentScan.Cli.Commands;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class ScanCorpusLiveCommandOracleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("silentscan-scan-corpus-live-oracle-").FullName;
    private readonly string _clonesRoot;

    public ScanCorpusLiveCommandOracleTests() => _clonesRoot = Path.Combine(_root, "clones");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WriteFile(string repoDirName, string relativePath, string contents)
    {
        var fullPath = Path.Combine(_clonesRoot, repoDirName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents);
    }

    private string WriteManifest(string json)
    {
        var manifestPath = Path.Combine(_root, $"manifest-{Guid.NewGuid():N}.json");
        File.WriteAllText(manifestPath, json);
        return manifestPath;
    }

    [Fact]
    public async Task RunAsync_RepoDeploysSuccessfully_ReturnsZeroAndIncludesRepoInJsonReport()
    {
        WriteFile("alpha", "Tables/Item.sql", "CREATE TABLE dbo.Item (ItemId INT NOT NULL PRIMARY KEY);");
        WriteFile("alpha", "Procedures/usp_GetItem.sql", """
            CREATE PROCEDURE dbo.usp_GetItem AS
            BEGIN
                SELECT ItemId FROM dbo.Item;
            END
            """);

        var manifestPath = WriteManifest("""
            {
              "repos": [
                {
                  "name": "alpha",
                  "url": "https://example.invalid/alpha",
                  "commitSha": "000000000000000000000000000000000000000a",
                  "license": "MIT",
                  "ddlPaths": ["Tables/*.sql"],
                  "procPaths": ["Procedures/*.sql"]
                }
              ]
            }
            """);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var options = new ReportOptions("json", "high", null, "brief");

        var exitCode = await ScanCorpusLiveCommand.RunAsync(manifestPath, _clonesRoot, stdout, stderr, options, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());

        using var document = JsonDocument.Parse(stdout.ToString());
        var repoResult = document.RootElement.GetProperty("alpha");
        Assert.Equal(1, repoResult.GetProperty("ModulesAnalyzed").GetInt32());
    }

    [Fact]
    public async Task RunAsync_MixOfScannedAndMissingClone_ReturnsOneAndSeparatesThemInTextReport()
    {
        WriteFile("present", "Tables/Item.sql", "CREATE TABLE dbo.Item (ItemId INT NOT NULL PRIMARY KEY);");

        var manifestPath = WriteManifest("""
            {
              "repos": [
                {
                  "name": "present-repo",
                  "url": "https://example.invalid/present",
                  "commitSha": "000000000000000000000000000000000000000a",
                  "license": "MIT",
                  "ddlPaths": ["Tables/*.sql"]
                },
                {
                  "name": "missing-repo",
                  "url": "https://example.invalid/missing",
                  "commitSha": "000000000000000000000000000000000000000b",
                  "license": "MIT",
                  "ddlPaths": ["Tables/*.sql"]
                }
              ]
            }
            """);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var options = new ReportOptions("text", "high", null, "brief");

        var exitCode = await ScanCorpusLiveCommand.RunAsync(manifestPath, _clonesRoot, stdout, stderr, options, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("'missing-repo' has no local clone at", stderr.ToString());
        Assert.DoesNotContain("'present-repo' has no local clone at", stderr.ToString());

        var report = stdout.ToString();
        Assert.Contains("present-repo", report);
        Assert.Contains("no local clone was found", report);
        Assert.Contains("missing-repo", report);
    }

    [Fact]
    public async Task RunAsync_DisallowedStatementInDdlFile_WarnsWithRepoPrefixButStillReturnsZero()
    {
        WriteFile("beta", "Tables/Item.sql", """
            CREATE TABLE dbo.Item (ItemId INT NOT NULL PRIMARY KEY);
            GO
            INSERT INTO dbo.Item (ItemId) VALUES (1);
            """);

        var manifestPath = WriteManifest("""
            {
              "repos": [
                {
                  "name": "beta",
                  "url": "https://example.invalid/beta",
                  "commitSha": "000000000000000000000000000000000000000c",
                  "license": "MIT",
                  "ddlPaths": ["Tables/*.sql"]
                }
              ]
            }
            """);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var options = new ReportOptions("text", "high", null, "brief");

        var exitCode = await ScanCorpusLiveCommand.RunAsync(manifestPath, _clonesRoot, stdout, stderr, options, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("warning: 'beta' ", stderr.ToString());
        Assert.Contains("InsertStatement", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_FileBelowDialectSniffingThreshold_WarnsWithReducedConfidenceButReturnsZero()
    {
        WriteFile("delta", "Tables/Item.sql", "CREATE TABLE dbo.Item (ItemId INT NOT NULL PRIMARY KEY);");
        WriteFile("delta", "Tables/NotSql.sql", "THIS IS $$$ NOT VALID T-SQL AT ALL ((((");

        var manifestPath = WriteManifest("""
            {
              "repos": [
                {
                  "name": "delta",
                  "url": "https://example.invalid/delta",
                  "commitSha": "000000000000000000000000000000000000000e",
                  "license": "MIT",
                  "ddlPaths": ["Tables/*.sql"]
                }
              ]
            }
            """);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var options = new ReportOptions("json", "high", null, "brief");

        var exitCode = await ScanCorpusLiveCommand.RunAsync(manifestPath, _clonesRoot, stdout, stderr, options, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("warning: 'delta' parse success rate", stderr.ToString());
        Assert.Contains("dialect-sniffing threshold", stderr.ToString());

        using var document = JsonDocument.Parse(stdout.ToString());
        var health = document.RootElement.GetProperty("delta").GetProperty("Report").GetProperty("ParseHealth");
        Assert.False(health.GetProperty("PassesDialectSniffing").GetBoolean());
        Assert.Equal(0.5, health.GetProperty("ParseSuccessRate").GetDouble());
    }

    [Fact]
    public async Task RunAsync_MixedGoodAndBrokenBatchInOneFile_WarnsThatBatchWasDroppedButReturnsZero()
    {
        for (var i = 0; i < 9; i++)
        {
            WriteFile("epsilon", $"Tables/Padding{i}.sql", $"CREATE VIEW dbo.vw_Padding{i} AS SELECT 1 AS X;");
        }

        WriteFile("epsilon", "Tables/Mixed.sql", """
            CREATE VIEW dbo.vw_First AS SELECT 1 AS X;
            GO
            CREATE PROCEDURE dbo.usp_Broken AS SELECT 1 FROM FROM;
            GO
            CREATE VIEW dbo.vw_Third AS SELECT 1 AS X;
            """);

        var manifestPath = WriteManifest("""
            {
              "repos": [
                {
                  "name": "epsilon",
                  "url": "https://example.invalid/epsilon",
                  "commitSha": "000000000000000000000000000000000000000f",
                  "license": "MIT",
                  "ddlPaths": ["Tables/*.sql"]
                }
              ]
            }
            """);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var options = new ReportOptions("text", "high", null, "brief");

        var exitCode = await ScanCorpusLiveCommand.RunAsync(manifestPath, _clonesRoot, stdout, stderr, options, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("dialect-sniffing threshold", stderr.ToString());
        Assert.Contains("a batch failed to parse and was dropped", stderr.ToString());
        Assert.Contains("Procedure 'dbo.usp_Broken'", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_RepoDeploymentThrows_WritesDeployErrorAndReturnsOne()
    {
        WriteFile("zeta", "Tables/Item.sql", "CREATE TABLE dbo.Item (ItemId INT NOT NULL PRIMARY KEY);");

        var manifestPath = WriteManifest("""
            {
              "repos": [
                {
                  "name": "zeta",
                  "url": "https://example.invalid/zeta",
                  "commitSha": "0000000000000000000000000000000000000001",
                  "license": "MIT",
                  "ddlPaths": ["Tables/*.sql"],
                  "declaredCollation": "NotARealCollationXyz"
                }
              ]
            }
            """);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var options = new ReportOptions("json", "high", null, "brief");

        var exitCode = await ScanCorpusLiveCommand.RunAsync(manifestPath, _clonesRoot, stdout, stderr, options, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("'zeta' could not be deployed/scanned", stderr.ToString());
        Assert.Equal("{}", stdout.ToString().Trim());
    }
}
