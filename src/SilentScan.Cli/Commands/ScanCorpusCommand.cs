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

            var files = CorpusFileResolver.ResolveAllFiles(repo, repoRoot);
            var parseResults = files.Select(f => ParseCorpusFile(repo, f)).ToList();
            var report = ScanReportBuilder.BuildFromParseResults(parseResults, repo.DeclaredCollation);

            // CLAUDE.md's corpus-admission criterion, actually consulted rather than merely
            // computed and displayed (an audit finding: ParseHealthReport.ParseSuccessRate
            // existed and was even documented as "the corpus dialect-sniffing signal," but
            // nothing checked it against the >= 90% bar it names) - a repo whose SQL turned out
            // to be mostly a different dialect would previously scan exactly as "successfully"
            // as a clean one, silently. This warns rather than skips the repo outright: the
            // repo was still deliberately curated into corpus/manifest.json, so its findings
            // are reported either way, but a reader now gets the same honest signal CLAUDE.md
            // promises instead of having to notice a low ParseSuccessRate buried in the JSON.
            if (!report.ParseHealth.PassesDialectSniffing)
            {
                stderr.WriteLine(
                    $"warning: '{repo.Name}' parse success rate {report.ParseHealth.ParseSuccessRate:P1} is below the " +
                    $"{ParseHealthReport.MinimumAcceptableParseSuccessRate:P0} dialect-sniffing threshold ({report.ParseHealth.FilesWithErrors} of {report.ParseHealth.TotalFiles} files had parse errors) - findings are still reported, but treat them with reduced confidence.");
                hadDialectSniffingFailure = true;
            }

            // A repo with no declaredCollation makes the flagship varchar-vs-nvarchar rule
            // structurally unreachable (VerdictClassifier: unresolved collation -> UNKNOWN,
            // never a guess) - re-running under both collation-family assumptions turns that
            // silent UNKNOWN into an honest "here's what it would be either way" (CLAUDE.md
            // precision discipline: an unqualified UNKNOWN looks identical to "nothing here,"
            // which is a different and stronger claim than what was actually established).
            var collationSensitivity = repo.DeclaredCollation is null
                ? CollationSensitivityReport.Analyze(parseResults)
                : null;

            reportsByRepo[repo.Name] = new CorpusRepoScanResult(report, collationSensitivity);
            readableRepos.Add(new ReadableCorpusRepo(repo.Name, report, collationSensitivity, repoRoot));
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
