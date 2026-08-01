using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using SilentScan.Core.Corpus;
using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;

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

        var command = new Command("scan-corpus", "Scan every repo declared in the corpus manifest and report per-repo findings.")
        {
            manifestOption,
            clonesRootOption,
        };

        command.SetAction(parseResult =>
        {
            var manifestPath = parseResult.GetValue(manifestOption)!;
            var clonesRoot = parseResult.GetValue(clonesRootOption)!;
            return Run(manifestPath, clonesRoot, Console.Out, Console.Error);
        });

        return command;
    }

    internal static int Run(string manifestPath, string clonesRoot, TextWriter stdout, TextWriter stderr)
    {
        if (!File.Exists(manifestPath))
        {
            stderr.WriteLine($"error: manifest not found: {manifestPath}");
            return 1;
        }

        var manifest = CorpusManifestLoader.Load(manifestPath);
        var reportsByRepo = new SortedDictionary<string, ScanReport>(StringComparer.Ordinal);
        var hadMissingRepo = false;

        foreach (var repo in manifest.Repos)
        {
            var repoRoot = Path.Combine(clonesRoot, RepoDirectoryName(repo.Url));
            if (!Directory.Exists(repoRoot))
            {
                stderr.WriteLine($"warning: '{repo.Name}' has no local clone at {repoRoot} - skipped.");
                hadMissingRepo = true;
                continue;
            }

            var files = CorpusFileResolver.ResolveAllFiles(repo, repoRoot);
            var parseResults = files.Select(f => ParseCorpusFile(repo, f)).ToList();
            reportsByRepo[repo.Name] = ScanReportBuilder.BuildFromParseResults(parseResults, repo.DeclaredCollation);
        }

        stdout.WriteLine(JsonSerializer.Serialize(reportsByRepo, JsonOptions));

        return hadMissingRepo ? 1 : 0;
    }

    private static SqlParseResult ParseCorpusFile(CorpusRepoEntry repo, string path)
    {
        var text = File.ReadAllText(path);
        text = CorpusTemplatePreprocessor.Apply(repo.TemplateSubstitutions, text);
        return SqlScriptParser.ParseText(path, text);
    }

    private static string RepoDirectoryName(string url) => url.TrimEnd('/').Split('/')[^1];
}
