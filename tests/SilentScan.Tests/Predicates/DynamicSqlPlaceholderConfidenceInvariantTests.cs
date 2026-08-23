using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class DynamicSqlPlaceholderConfidenceInvariantTests
{
    public static TheoryData<string, string> PlaceholderBearingScenarios => new()
    {
        {
            "no-known-caller proc-parameter seeding (quoted position)",
            """
            CREATE TABLE dbo.Orders (Status VARCHAR(20) NOT NULL, INDEX IX_Status (Status));
            GO
            CREATE PROCEDURE dbo.usp_FindByStatus @Status NVARCHAR(20) AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM dbo.Orders WHERE Status = N''' + @Status + N'''';
                EXEC(@sql);
            END;
            """
        },
        {
            "uninitialized DECLARE (quoted position)",
            """
            CREATE TABLE dbo.Orders (Status VARCHAR(20) NOT NULL, INDEX IX_Status (Status));
            GO
            DECLARE @Status NVARCHAR(20);
            DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM dbo.Orders WHERE Status = N''' + @Status + N'''';
            EXEC(@sql);
            """
        },
        {
            "caller forwards a variable, not a literal (quoted position)",
            """
            CREATE TABLE dbo.Orders (Status VARCHAR(20) NOT NULL, INDEX IX_Status (Status));
            GO
            CREATE PROCEDURE dbo.usp_FindByStatus @Status NVARCHAR(20) AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM dbo.Orders WHERE Status = N''' + @Status + N'''';
                EXEC(@sql);
            END;
            GO
            CREATE PROCEDURE dbo.usp_Caller @IncomingStatus NVARCHAR(20) AS
            BEGIN
                EXEC dbo.usp_FindByStatus @Status = @IncomingStatus;
            END;
            """
        },
        {
            "mixed identifier- and quoted-position placeholders in one statement",
            """
            CREATE TABLE dbo.Orders (Status VARCHAR(20) NOT NULL, INDEX IX_Status (Status));
            GO
            CREATE PROCEDURE dbo.usp_JoinAndCheck @LogTableName SYSNAME, @Status NVARCHAR(20) AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) =
                    N'SELECT o.Status FROM dbo.Orders AS o CROSS JOIN ' + QUOTENAME(@LogTableName) +
                    N' AS lt WHERE o.Status = N''' + @Status + N'''';
                EXEC(@sql);
            END;
            """
        },
    };

    [Theory]
    [MemberData(nameof(PlaceholderBearingScenarios))]
    public async Task NoFindingOfAnyKindReportsHighConfidence(string scenarioName, string sql)
    {
        var report = await EngineAuthoritativeScan.ScanAsync(sql, "SQL_Latin1_General_CP1_CI_AS", minimumConfidence: FindingConfidence.Low);
        foreach (var file in report.ParseHealth.Files)
        {
            Assert.Empty(file.Errors);
        }

        var totalChecked =
            report.Tier1Findings.Count + report.TypedFindings.Count + report.ExpressionDerivedFindings.Count
            + report.CollationConflictFindings.Count + report.WriteLossFindings.Count;

        Assert.True(totalChecked > 0, $"Scenario \"{scenarioName}\" produced no findings of any kind to check.");

        Assert.All(report.Tier1Findings, f => Assert.NotEqual(FindingConfidence.High, f.Confidence));
        Assert.All(report.TypedFindings, f => Assert.NotEqual(FindingConfidence.High, f.Confidence));
        Assert.All(report.ExpressionDerivedFindings, f => Assert.NotEqual(FindingConfidence.High, f.Confidence));
        Assert.All(report.CollationConflictFindings, f => Assert.NotEqual(FindingConfidence.High, f.Confidence));
        Assert.All(report.WriteLossFindings, f => Assert.NotEqual(FindingConfidence.High, f.Confidence));
    }

private static readonly string[] AuthorizedConstructionSites = ["DynamicSqlTransfer.cs"];

    [Fact]
    public void EveryDynamicSqlScriptConstructor_ComputesConfidenceSolelyFromPlaceholderPresence()
    {
        foreach (var fileName in AuthorizedConstructionSites)
        {
            var source = File.ReadAllText(FindSourceFile(fileName));
            Assert.Equal(1, CountOccurrences(source, "new DynamicSqlScript("));
        }

        var repoRoot = FindRepoRoot();
        var otherConstructionSites = Directory
            .GetFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !AuthorizedConstructionSites.Any(name => f.EndsWith(name, StringComparison.Ordinal)))
            .Where(f => File.ReadAllText(f).Contains("new DynamicSqlScript(", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(otherConstructionSites);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string FindSourceFile(string fileName)
    {
        var repoRoot = FindRepoRoot();
        var matches = Directory.GetFiles(Path.Combine(repoRoot, "src"), fileName, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        return Assert.Single(matches);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SilentScan.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root from test base directory.");
    }
}
