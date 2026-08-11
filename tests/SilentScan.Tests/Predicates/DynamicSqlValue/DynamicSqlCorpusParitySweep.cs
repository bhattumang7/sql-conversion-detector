using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;

namespace SilentScan.Tests.Predicates.DynamicSqlValue;

/// <summary>
/// A manual diagnostic, deliberately NOT an <c>[Fact]</c> (xUnit would discover and run it on
/// every routine `dotnet test`/sonar-scan pass - several minutes, and it needs the corpus repos
/// cloned locally, docs/local-dev.md - a `[Fact(Skip=...)]` was tried first but Sonar's own
/// xUnit1004 flags a skipped test as a smell, so plain non-discovery is the clean way to keep
/// this committed without either cost). Sweeps every corpus .sql file directly through
/// <see cref="DynamicSqlScanner.Scan"/> and <see cref="DynamicSqlScannerV2.Scan"/> (bypassing DB
/// deployment/whitelist filtering - this scanner needs no catalog for its own pass) and reports
/// aggregate script/finding counts, a decline-reason histogram, and a sample of scripts the old
/// engine found that the new one didn't - real-world signal beyond
/// <see cref="DynamicSqlParityTests"/>'s curated scenarios, used to find the kind of gap that
/// produced <see cref="DynamicSqlParityTests.CursorFetchedTwice_FeedingCastInsideLoop_MatchesOldScanner"/>.
/// To run: temporarily add <c>[Fact]</c> above this method, then
/// `dotnet test --filter FullyQualifiedName~DynamicSqlCorpusParitySweep`, and read the thrown
/// exception's message for the report - throwing (rather than logging) is deliberate: it is the
/// simplest way to surface a multi-line report from a manual xUnit run.
/// </summary>
public sealed class DynamicSqlCorpusParitySweep
{
    public static void SweepCorpusAndReportDivergence()
    {
        var repoRoot = FindRepoRoot();
        var corpusRoot = Path.Combine(repoRoot, "corpus", "_clones");
        var files = Directory.Exists(corpusRoot) ? Directory.GetFiles(corpusRoot, "*.sql", SearchOption.AllDirectories) : [];
        Assert.True(files.Length > 0, $"No corpus files found under {corpusRoot} - see docs/local-dev.md to clone the pinned repos.");

        var oldScriptCount = 0;
        var newScriptCount = 0;
        var oldFindingCount = 0;
        var newFindingCount = 0;
        var oldReasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var newReasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var parseFailures = 0;
        var crashes = new List<string>();
        var scriptOnlyInOld = new List<string>();

        foreach (var file in files)
        {
            var parseResult = SqlScriptParser.ParseText(file, File.ReadAllText(file));
            if (parseResult.HasErrors)
            {
                parseFailures++;
                continue;
            }

            if (!TryScan(file, "OLD", () => DynamicSqlScanner.Scan(parseResult), crashes, out var oldResult)
                || !TryScan(file, "NEW", () => DynamicSqlScannerV2.Scan(parseResult), crashes, out var newResult))
            {
                continue;
            }

            oldScriptCount += oldResult.AnalyzableScripts.Count;
            newScriptCount += newResult.AnalyzableScripts.Count;
            oldFindingCount += oldResult.Findings.Count;
            newFindingCount += newResult.Findings.Count;
            Tally(oldReasons, oldResult.Findings);
            Tally(newReasons, newResult.Findings);

            var oldTexts = oldResult.AnalyzableScripts.Select(s => s.InnerText).ToHashSet(StringComparer.Ordinal);
            var newTexts = newResult.AnalyzableScripts.Select(s => s.InnerText).ToHashSet(StringComparer.Ordinal);
            scriptOnlyInOld.AddRange(oldTexts.Except(newTexts).Take(2).Select(missing => $"{Path.GetFileName(file)}: {Truncate(missing)}"));
        }

        throw new InvalidOperationException(BuildReport(
            files.Length, parseFailures, crashes, oldScriptCount, newScriptCount, oldFindingCount, newFindingCount, oldReasons, newReasons, scriptOnlyInOld));
    }

    private static bool TryScan(string file, string label, Func<DynamicSqlExtractionResult> scan, List<string> crashes, out DynamicSqlExtractionResult result)
    {
        try
        {
            result = scan();
            return true;
        }
        catch (Exception ex)
        {
            crashes.Add($"{label} {file}: {ex.GetType().Name}: {ex.Message}");
            result = null!;
            return false;
        }
    }

    private static void Tally(Dictionary<string, int> reasons, IReadOnlyList<DynamicSqlFinding> findings)
    {
        foreach (var f in findings)
        {
            var key = $"{f.Outcome}:{f.Reason}";
            reasons[key] = reasons.GetValueOrDefault(key) + 1;
        }
    }

    private static string BuildReport(
        int fileCount, int parseFailures, List<string> crashes, int oldScripts, int newScripts, int oldFindings, int newFindings,
        Dictionary<string, int> oldReasons, Dictionary<string, int> newReasons, List<string> scriptOnlyInOld)
    {
        var reasonKeys = oldReasons.Keys.Union(newReasons.Keys).OrderBy(k => k, StringComparer.Ordinal);
        var reasonReport = string.Join('\n', reasonKeys.Select(k => $"  {k}: old={oldReasons.GetValueOrDefault(k)} new={newReasons.GetValueOrDefault(k)}"));

        return
            $"files={fileCount} parseFailures={parseFailures} crashes={crashes.Count}\n" +
            $"scripts: old={oldScripts} new={newScripts}\n" +
            $"findings: old={oldFindings} new={newFindings}\n" +
            $"reasons:\n{reasonReport}\n" +
            $"scriptOnlyInOld (sample):\n{string.Join('\n', scriptOnlyInOld.Take(30))}\n" +
            $"crashes (sample):\n{string.Join('\n', crashes.Take(10))}";
    }

    private static string Truncate(string s) => s.Length > 120 ? s[..120] + "..." : s;

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
