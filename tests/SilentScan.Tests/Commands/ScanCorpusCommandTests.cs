using System.Text.Json;
using SilentScan.Cli.Commands;

namespace SilentScan.Tests.Commands;

public sealed class ScanCorpusCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"silentscan-scan-corpus-test-{Guid.NewGuid():N}");
    private readonly string _manifestPath;

    public ScanCorpusCommandTests()
    {
        Directory.CreateDirectory(_root);
        var cloneDir = Path.Combine(_root, "clones", "example");
        Directory.CreateDirectory(cloneDir);

        // Code carries a SQL_* collation, Name none - a varchar column compared against an
        // nvarchar value both with (Code) and without (Name) an explicit column collation, so
        // the collation-sensitivity re-run has something real to disagree about for Name while
        // Code's own explicit COLLATE overrides whatever collation assumption is fed in.
        File.WriteAllText(Path.Combine(cloneDir, "schema.sql"), """
            CREATE TABLE dbo.Customers
            (
                Code VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
                Name VARCHAR(20) NOT NULL
            );
            CREATE INDEX IX_Code ON dbo.Customers(Code);
            CREATE INDEX IX_Name ON dbo.Customers(Name);
            GO
            CREATE PROCEDURE dbo.Find @c NVARCHAR(20), @n NVARCHAR(20) AS
            SELECT 1 FROM dbo.Customers WHERE Code = @c;
            SELECT 1 FROM dbo.Customers WHERE Name = @n;
            GO
            """);

        _manifestPath = Path.Combine(_root, "manifest.json");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WriteManifest(string? declaredCollation)
    {
        var collationJson = declaredCollation is null ? "null" : $"\"{declaredCollation}\"";
        File.WriteAllText(_manifestPath, $$"""
            {
              "repos": [
                {
                  "name": "example",
                  "url": "https://github.com/example/example",
                  "commitSha": "abcdef0123456789abcdef0123456789abcdef01",
                  "license": "MIT",
                  "ddlPaths": ["*.sql"],
                  "procPaths": ["*.sql"],
                  "declaredCollation": {{collationJson}}
                }
              ]
            }
            """);
    }

    [Fact]
    public void Run_MissingManifest_ReturnsOneAndWritesError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = ScanCorpusCommand.Run("/no/such/manifest.json", Path.Combine(_root, "clones"), stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("manifest not found", stderr.ToString());
    }

    [Fact]
    public void Run_NoDeclaredCollation_IncludesCollationSensitivityShowingBothAssumptions()
    {
        WriteManifest(declaredCollation: null);
        var stdout = new StringWriter();

        ScanCorpusCommand.Run(_manifestPath, Path.Combine(_root, "clones"), stdout, new StringWriter());

        using var document = JsonDocument.Parse(stdout.ToString());
        var repoResult = document.RootElement.GetProperty("example");
        var sensitivity = repoResult.GetProperty("CollationSensitivity");

        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", sensitivity.GetProperty("SqlFamilyCollation").GetString());
        Assert.Equal("Latin1_General_CI_AS", sensitivity.GetProperty("WindowsFamilyCollation").GetString());

        // Name has no explicit COLLATE, so it inherits the fed-in database-default assumption -
        // ScanForced under SQL_* (no dynamic range seek for varchar-vs-nvarchar), RangeSeek
        // under Windows (GetRangeThroughConvert is available). Code's own explicit COLLATE
        // always wins over the fed-in default, so it contributes the SAME ScanForced finding to
        // BOTH runs regardless of which collation assumption is active.
        var underSql = sensitivity.GetProperty("UnderSqlFamilyAssumption");
        var underWindows = sensitivity.GetProperty("UnderWindowsFamilyAssumption");

        Assert.Equal(2, underSql.GetProperty("ScanForcedCount").GetInt32());
        Assert.Equal(0, underSql.GetProperty("RangeSeekCount").GetInt32());

        Assert.Equal(1, underWindows.GetProperty("ScanForcedCount").GetInt32());
        Assert.Equal(1, underWindows.GetProperty("RangeSeekCount").GetInt32());
    }

    [Fact]
    public void Run_DeclaredCollationPinned_OmitsCollationSensitivity()
    {
        WriteManifest(declaredCollation: "SQL_Latin1_General_CP1_CI_AS");
        var stdout = new StringWriter();

        ScanCorpusCommand.Run(_manifestPath, Path.Combine(_root, "clones"), stdout, new StringWriter());

        using var document = JsonDocument.Parse(stdout.ToString());
        var repoResult = document.RootElement.GetProperty("example");

        Assert.Equal(JsonValueKind.Null, repoResult.GetProperty("CollationSensitivity").ValueKind);
    }

    [Fact]
    public void Run_RepoBelowDialectSniffingThreshold_WarnsAndReturnsOne()
    {
        // A repo whose SQL is mostly a different dialect entirely - CLAUDE.md's corpus
        // dialect-sniffing criterion ("ScriptDOM parse success >= 90% of files"), which
        // previously was computed and displayed but never actually gated on anything.
        var cloneDir = Path.Combine(_root, "clones", "example");
        File.WriteAllText(Path.Combine(cloneDir, "not_tsql.sql"), "THIS IS $$$ NOT VALID T-SQL AT ALL ((((");

        WriteManifest(declaredCollation: "SQL_Latin1_General_CP1_CI_AS");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = ScanCorpusCommand.Run(_manifestPath, Path.Combine(_root, "clones"), stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("dialect-sniffing threshold", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_RepoAtFullParseSuccess_NoDialectSniffingWarning()
    {
        WriteManifest(declaredCollation: "SQL_Latin1_General_CP1_CI_AS");
        var stderr = new StringWriter();

        var exitCode = ScanCorpusCommand.Run(_manifestPath, Path.Combine(_root, "clones"), new StringWriter(), stderr);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("dialect-sniffing", stderr.ToString(), StringComparison.Ordinal);
    }
}
