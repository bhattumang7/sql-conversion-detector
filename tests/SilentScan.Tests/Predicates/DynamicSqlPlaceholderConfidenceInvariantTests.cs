using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// The standing auditor guarantee behind every symbolic-placeholder capability landed across
/// WP0-WP4/WP6: no finding of ANY kind may ever report <see cref="FindingConfidence.High"/> when
/// it derives - directly or through nested dynamic SQL - from a script whose text rests on a
/// symbolic placeholder rather than a proven literal. This is not tested indirectly through one
/// or two scenario-specific assertions elsewhere; it is tested here, explicitly, across every
/// distinct mechanism that currently introduces a placeholder (no-known-caller proc-parameter
/// seeding, an uninitialized DECLARE, a variable-forwarded caller argument, and the mixed
/// quoted/identifier-position folding from the per-occurrence placeholder classifier) - so that a
/// future change to any ONE of those producers, or a brand new producer, is caught here rather
/// than surfacing as a silently-wrong High-confidence number in a published study. The structural
/// guarantee this protects (<see cref="Predicates.DynamicSqlScript"/>'s own doc comment) is that
/// <c>the dynamic SQL engine's own script-building logic</c> is the ONE place a script's <c>Confidence</c> is computed,
/// and <c>DynamicSqlPipeline</c>'s Remap/RemapNested are the ONLY places that stamp it onto a
/// finding - this test exercises that path end-to-end via the real production entry point
/// (<see cref="EngineAuthoritativeScan"/>), not by re-deriving the invariant from the internals.
/// </summary>
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

        // A scenario contributing zero findings across every kind proves nothing - it would let
        // a future edit silently break the scenario's own SQL (e.g. the predicate stops
        // resolving to a real column) while this test kept passing vacuously.
        Assert.True(totalChecked > 0, $"Scenario \"{scenarioName}\" produced no findings of any kind to check.");

        Assert.All(report.Tier1Findings, f => Assert.NotEqual(FindingConfidence.High, f.Confidence));
        Assert.All(report.TypedFindings, f => Assert.NotEqual(FindingConfidence.High, f.Confidence));
        Assert.All(report.ExpressionDerivedFindings, f => Assert.NotEqual(FindingConfidence.High, f.Confidence));
        Assert.All(report.CollationConflictFindings, f => Assert.NotEqual(FindingConfidence.High, f.Confidence));
        Assert.All(report.WriteLossFindings, f => Assert.NotEqual(FindingConfidence.High, f.Confidence));
    }

    /// <summary>
    /// <see cref="DynamicSqlTransfer"/> is the dynamic-SQL engine's sole EXEC/sp_executesql
    /// emission choke point - it derives Confidence via the "does this assembly contain a Hole"
    /// rule (<see cref="Predicates.DynamicSqlValue.SqlTextValue.ContainsHole"/>), not a
    /// copy-pasted-and-forgotten one. The old scanner (a second, temporary construction site kept
    /// alive only for the duration of the V1-to-V2 rebuild) is deleted, so this set is back down
    /// to exactly the one legitimate site.
    /// </summary>
    private static readonly string[] AuthorizedConstructionSites = ["DynamicSqlTransfer.cs"];

    [Fact]
    public void EveryDynamicSqlScriptConstructor_ComputesConfidenceSolelyFromPlaceholderPresence()
    {
        // Guards the choke point this whole invariant rests on: only the authorized site(s) above
        // may construct a DynamicSqlScript. If a THIRD construction site is ever added and forgets
        // to derive Confidence from placeholder/hole presence the same way, this test starts
        // failing the moment it does - a compile-time-cheap tripwire against the exact mistake
        // this whole task exists to rule out.
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
