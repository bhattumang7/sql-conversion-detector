using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using SilentScan.Core.Corpus;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Readable;

namespace SilentScan.Cli.Commands;

/// <summary>
/// `silentscan scan-corpus` — scans every repo declared in corpus/manifest.json (CLAUDE.md
/// Phase 4/6), resolving each entry's ddlPaths/procPaths globs against its local clone
/// rather than a blind directory walk (a plain "*.sql" scan silently misses repos like
/// DNN Platform, whose corpus lives in *.SqlDataProvider files).
/// </summary>
public static class ScanCorpusCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Command Create()
    {
        var manifestOption = new Option<string>("--manifest")
        {
            Description = "Path to corpus/manifest.json.",
            DefaultValueFactory = _ => "corpus/manifest.json",
        };

        var clonesRootOption = new Option<string>("--clones-root")
        {
            Description = "Directory containing each repo's local clone, one subdirectory per manifest entry name (or the repo URL's last path segment if names differ).",
            DefaultValueFactory = _ => "corpus/_clones",
        };

        var formatOption = new Option<string>("--format")
        {
            Description = ReportOutput.FormatOptionDescription,
            DefaultValueFactory = _ => "text",
        };

        var outputOption = new Option<string?>("--output")
        {
            Description = ReportOutput.OutputOptionDescription,
        };

        var command = new Command("scan-corpus", "Scan every repo declared in the corpus manifest and report per-repo findings.")
        {
            manifestOption,
            clonesRootOption,
            formatOption,
            outputOption,
        };

        command.SetAction(parseResult =>
        {
            var manifestPath = parseResult.GetValue(manifestOption)!;
            var clonesRoot = parseResult.GetValue(clonesRootOption)!;
            var format = parseResult.GetValue(formatOption)!;
            var output = parseResult.GetValue(outputOption);
            return Run(manifestPath, clonesRoot, Console.Out, Console.Error, format, output);
        });

        return command;
    }

    internal static int Run(string manifestPath, string clonesRoot, TextWriter stdout, TextWriter stderr, string format = "text", string? outputPath = null)
    {
        if (!File.Exists(manifestPath))
        {
            stderr.WriteLine($"error: manifest not found: {manifestPath}");
            return 1;
        }

        if (!ReportOutput.TryParseFormat(format, out var reportFormat))
        {
            stderr.WriteLine(ReportOutput.UnknownFormatMessage(format));
            return 1;
        }

        // A corpus run produces one report per repo, and a SARIF log describes a single run
        // against a single tree - there is no honest way to merge five repos' findings into one
        // without losing which repo each came from.
        if (reportFormat == ReportFormat.Sarif)
        {
            stderr.WriteLine("error: scan-corpus does not support --format sarif; run `scan` against a single repo for a SARIF log.");
            return 1;
        }

        var manifest = CorpusManifestLoader.Load(manifestPath);
        var reportsByRepo = new SortedDictionary<string, CorpusRepoScanResult>(StringComparer.Ordinal);
        var readableRepos = new List<ReadableCorpusRepo>();
        var missingRepos = new List<string>();
        var hadMissingRepo = false;
        var hadDialectSniffingFailure = false;

        // Each repo's scan is completely independent of every other's - its own catalog, its own
        // parse results, no shared state at all - so the CPU-heavy work (parsing + the full
        // catalog/lineage/predicate pipeline + collation sensitivity) runs in parallel across
        // repos (CLAUDE.md roadmap: "scale the scan pipeline"). The missing-clone check stays
        // sequential first (fast, and needs to run before anything expensive), and every
        // stderr write/shared-collection update happens afterward, in a single-threaded pass
        // over the parallel results sorted by repo name - so warning output and the aggregated
        // report stay exactly as deterministic as a plain sequential loop would produce.
        var existingRepos = new List<(CorpusRepoEntry Repo, string RepoRoot)>();
        foreach (var repo in manifest.Repos)
        {
            var repoRoot = Path.Combine(clonesRoot, RepoDirectoryName(repo.Url));
            if (!Directory.Exists(repoRoot))
            {
                stderr.WriteLine($"warning: '{repo.Name}' has no local clone at {repoRoot} - skipped.");
                missingRepos.Add(repo.Name);
                hadMissingRepo = true;
                continue;
            }

            existingRepos.Add((repo, repoRoot));
        }

        var scannedRepos = existingRepos
            .AsParallel()
            .Select(entry => ScanOneRepo(entry.Repo, entry.RepoRoot))
            .ToList();

        foreach (var scanned in scannedRepos.OrderBy(s => s.Repo.Name, StringComparer.Ordinal))
        {
            // CLAUDE.md's corpus-admission criterion, actually consulted rather than merely
            // computed and displayed (an audit finding: ParseHealthReport.ParseSuccessRate
            // existed and was even documented as "the corpus dialect-sniffing signal," but
            // nothing checked it against the >= 90% bar it names) - a repo whose SQL turned out
            // to be mostly a different dialect would previously scan exactly as "successfully"
            // as a clean one, silently. This warns rather than skips the repo outright: the
            // repo was still deliberately curated into corpus/manifest.json, so its findings
            // are reported either way, but a reader now gets the same honest signal CLAUDE.md
            // promises instead of having to notice a low ParseSuccessRate buried in the JSON.
            if (!scanned.Report.ParseHealth.PassesDialectSniffing)
            {
                stderr.WriteLine(
                    $"warning: '{scanned.Repo.Name}' parse success rate {scanned.Report.ParseHealth.ParseSuccessRate:P1} is below the " +
                    $"{ParseHealthReport.MinimumAcceptableParseSuccessRate:P0} dialect-sniffing threshold ({scanned.Report.ParseHealth.FilesWithErrors} of {scanned.Report.ParseHealth.TotalFiles} files had parse errors) - findings are still reported, but treat them with reduced confidence.");
                hadDialectSniffingFailure = true;
            }

            reportsByRepo[scanned.Repo.Name] = new CorpusRepoScanResult(scanned.Report, scanned.CollationSensitivity);
            readableRepos.Add(new ReadableCorpusRepo(scanned.Repo.Name, scanned.Report, scanned.CollationSensitivity, scanned.RepoRoot));
        }

        var content = reportFormat == ReportFormat.Json
            ? JsonSerializer.Serialize(reportsByRepo, JsonOptions)
            : ReadableCorpusReportWriter.Write(
                [.. readableRepos.OrderBy(r => r.Name, StringComparer.Ordinal)],
                [.. missingRepos.OrderBy(name => name, StringComparer.Ordinal)],
                ReportOutput.ToStyle(reportFormat));

        if (!ReportOutput.Emit(content, outputPath, stdout, stderr))
        {
            return 1;
        }

        return hadMissingRepo || hadDialectSniffingFailure ? 1 : 0;
    }

    /// <summary>One repo's full scan (parse + catalog/lineage/predicate pipeline + collation sensitivity) - self-contained and side-effect-free, so it's safe to run concurrently with every other repo's.</summary>
    private static ScannedRepo ScanOneRepo(CorpusRepoEntry repo, string repoRoot)
    {
        var files = CorpusFileResolver.ResolveAllFiles(repo, repoRoot);
        var parseResults = files.Select(f => ParseCorpusFile(repo, f)).ToList();
        var report = ScanReportBuilder.BuildFromParseResults(parseResults, repo.DeclaredCollation, manifestTempdbCollation: repo.TempdbCollation);

        // A repo with no declaredCollation makes the flagship varchar-vs-nvarchar rule
        // structurally unreachable (VerdictClassifier: unresolved collation -> UNKNOWN,
        // never a guess) - re-running under both collation-family assumptions turns that
        // silent UNKNOWN into an honest "here's what it would be either way" (CLAUDE.md
        // precision discipline: an unqualified UNKNOWN looks identical to "nothing here,"
        // which is a different and stronger claim than what was actually established).
        var collationSensitivity = repo.DeclaredCollation is null
            ? CollationSensitivityReport.Analyze(parseResults)
            : null;

        return new ScannedRepo(repo, repoRoot, report, collationSensitivity);
    }

    private sealed record ScannedRepo(CorpusRepoEntry Repo, string RepoRoot, ScanReport Report, CollationSensitivityReport? CollationSensitivity);

    private static SqlParseResult ParseCorpusFile(CorpusRepoEntry repo, string path)
    {
        // Routes through the same BOM-detection/Latin-1 fallback ParseFile uses (an audit
        // finding: this was the one path that actually determines the published study's
        // numbers, and it was bypassing that recovery entirely via a plain File.ReadAllText).
        var text = SqlScriptParser.DecodeFile(path);
        text = CorpusTemplatePreprocessor.Apply(repo.TemplateSubstitutions, text);
        return SqlScriptParser.ParseText(path, text);
    }

    private static string RepoDirectoryName(string url) => url.TrimEnd('/').Split('/')[^1];
}

/// <summary>One repo's ordinary scan report, plus a collation-sensitivity re-run when the manifest pinned no collation for it (null when one was pinned - there's nothing to be sensitive to).</summary>
public sealed record CorpusRepoScanResult(ScanReport Report, CollationSensitivityReport? CollationSensitivity);
