using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using SilentScan.Core.Corpus;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Readable;
using SilentScan.Live.Corpus;
using SilentScan.Verify;

namespace SilentScan.Cli.Commands;

/// <summary>
/// `silentscan scan-corpus-live` — the engine-authoritative counterpart to <c>scan-corpus</c>
/// (roadmap "make the corpus catalog engine-authoritative": CLAUDE.md hard scope, "corpus
/// scanning deploys the repo's (whitelist-filtered) DDL to the disposable Docker instance, then
/// reads the catalog and module text back out"). A separate command rather than a flag on
/// <c>scan-corpus</c> - the roadmap's own next item is deleting the file-parsed catalog path
/// entirely once this one is trusted, which is a clean removal only if the old command was never
/// mutated in place. Requires the disposable Docker SQL Server oracle (docs/local-dev.md) -
/// unlike <c>scan-corpus</c>, this actually deploys DDL and connects.
/// </summary>
public static class ScanCorpusLiveCommand
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

        var command = new Command(
            "scan-corpus-live",
            "Deploy every repo declared in the corpus manifest to the disposable Docker oracle, read its catalog and module text back from the engine, and report per-repo findings.")
        {
            manifestOption,
            clonesRootOption,
            formatOption,
            outputOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var manifestPath = parseResult.GetValue(manifestOption)!;
            var clonesRoot = parseResult.GetValue(clonesRootOption)!;
            var format = parseResult.GetValue(formatOption)!;
            var output = parseResult.GetValue(outputOption);
            return await RunAsync(manifestPath, clonesRoot, Console.Out, Console.Error, format, output, cancellationToken);
        });

        return command;
    }

    internal static async Task<int> RunAsync(
        string manifestPath, string clonesRoot, TextWriter stdout, TextWriter stderr, string format = "text",
        string? outputPath = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(manifestPath))
        {
            await stderr.WriteLineAsync($"error: manifest not found: {manifestPath}");
            return 1;
        }

        if (!ReportOutput.TryParseFormat(format, out var reportFormat))
        {
            await stderr.WriteLineAsync(ReportOutput.UnknownFormatMessage(format));
            return 1;
        }

        if (reportFormat == ReportFormat.Sarif)
        {
            await stderr.WriteLineAsync("error: scan-corpus-live does not support --format sarif; run `scan-db` against a single database for a SARIF log.");
            return 1;
        }

        var manifest = CorpusManifestLoader.Load(manifestPath);
        var sqlOptions = SqlServerOptions.LocalDocker;
        var reportsByRepo = new SortedDictionary<string, CorpusLiveRepoResult>(StringComparer.Ordinal);
        var readableRepos = new List<ReadableCorpusRepo>();
        var missingRepos = new List<string>();
        var hadMissingRepo = false;
        var hadUnexpectedFailure = false;

        // Deliberately sequential, not AsParallel like scan-corpus - every repo here creates and
        // drops its OWN disposable database on the SAME shared Docker instance, and this
        // session's own earlier RCA (LivePlanCacheReader flakiness) found real, reproducible
        // failure modes from exactly this kind of concurrent DDL churn against one instance.
        // GUID-suffixed database names (CorpusLiveScanRunner) make concurrent runs SAFE from
        // collisions, but not free of that churn - running repos one at a time keeps this
        // command's own footprint on the instance no worse than a single verify-corpus run.
        foreach (var repo in manifest.Repos)
        {
            var repoRoot = Path.Combine(clonesRoot, RepoDirectoryName(repo.Url));
            if (!Directory.Exists(repoRoot))
            {
                await stderr.WriteLineAsync($"warning: '{repo.Name}' has no local clone at {repoRoot} - skipped.");
                missingRepos.Add(repo.Name);
                hadMissingRepo = true;
                continue;
            }

            var outcome = await ScanOneRepoAsync(repo, repoRoot, sqlOptions, stderr, cancellationToken);
            if (outcome is null)
            {
                hadUnexpectedFailure = true;
                continue;
            }

            reportsByRepo[repo.Name] = outcome;
            readableRepos.Add(new ReadableCorpusRepo(repo.Name, outcome.Report, CollationSensitivity: null, repoRoot));
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

        return hadMissingRepo || hadUnexpectedFailure ? 1 : 0;
    }

    /// <summary>Deploys/scans one repo and reports every diagnostic to <paramref name="stderr"/> - null return means an unexpected deploy/scan failure (not a missing clone, which the caller already handled), never a silent "nothing to report."</summary>
    private static async Task<CorpusLiveRepoResult?> ScanOneRepoAsync(
        CorpusRepoEntry repo, string repoRoot, SqlServerOptions sqlOptions, TextWriter stderr, CancellationToken cancellationToken)
    {
        CorpusLiveRepoResult result;
        try
        {
            result = await CorpusLiveScanRunner.RunAsync(repo, repoRoot, sqlOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            await stderr.WriteLineAsync($"error: '{repo.Name}' could not be deployed/scanned: {ex.Message}");
            return null;
        }

        foreach (var message in result.DeploymentMessages)
        {
            await stderr.WriteLineAsync($"warning: '{repo.Name}' {message}");
        }

        foreach (var unmapped in result.UnmappedModules)
        {
            await stderr.WriteLineAsync($"warning: '{repo.Name}' could not map deployed module '{unmapped}' back to its defining file - reported under its bare qualified name instead.");
        }

        if (!result.Report.ParseHealth.PassesDialectSniffing)
        {
            await stderr.WriteLineAsync(
                $"warning: '{repo.Name}' parse success rate {result.Report.ParseHealth.ParseSuccessRate:P1} is below the " +
                $"{ParseHealthReport.MinimumAcceptableParseSuccessRate:P0} dialect-sniffing threshold - findings are still reported, but treat them with reduced confidence.");
        }

        return result;
    }

    private static string RepoDirectoryName(string url) => url.TrimEnd('/').Split('/')[^1];
}
