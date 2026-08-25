using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using SilentScan.Core.Corpus;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Readable;
using SilentScan.Live.Corpus;
using SilentScan.Verify;

namespace SilentScan.Cli.Commands;

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

        var confidenceOption = new Option<string>("--confidence")
        {
            Description = ReportOutput.ConfidenceOptionDescription,
            DefaultValueFactory = _ => "high",
        };

        var verbosityOption = new Option<string>("--verbosity")
        {
            Description = ReportOutput.VerbosityOptionDescription,
            DefaultValueFactory = _ => "brief",
        };

        var strictOption = new Option<bool>("--strict")
        {
            Description = ReportOutput.StrictOptionDescription,
            DefaultValueFactory = _ => false,
        };

        var command = new Command(
            "scan-corpus-live",
            "Deploy every repo declared in the corpus manifest to the disposable Docker oracle, read its catalog and module text back from the engine, and report per-repo findings.")
        {
            manifestOption,
            clonesRootOption,
            formatOption,
            confidenceOption,
            verbosityOption,
            strictOption,
            outputOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var manifestPath = parseResult.GetValue(manifestOption)!;
            var clonesRoot = parseResult.GetValue(clonesRootOption)!;
            var strict = parseResult.GetValue(strictOption);
            var options = new ReportOptions(
                parseResult.GetValue(formatOption)!,
                parseResult.GetValue(confidenceOption)!,
                parseResult.GetValue(outputOption),
                parseResult.GetValue(verbosityOption)!);
            return await RunAsync(manifestPath, clonesRoot, strict, Console.Out, Console.Error, options, cancellationToken);
        });

        return command;
    }

    internal static async Task<int> RunAsync(
        string manifestPath, string clonesRoot, bool strict, TextWriter stdout, TextWriter stderr, ReportOptions options, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(manifestPath))
        {
            await stderr.WriteLineAsync($"error: manifest not found: {manifestPath}");
            return 1;
        }

        var validated = await ValidateOptionsAsync(options, stderr);
        if (validated.ExitCode is { } earlyExitCode)
        {
            return earlyExitCode;
        }

        var manifest = CorpusManifestLoader.Load(manifestPath);
        var sqlOptions = SqlServerOptions.LocalDocker;
        var scan = await ScanAllReposAsync(manifest, clonesRoot, sqlOptions, stderr, validated.Confidence, cancellationToken);

        var content = validated.Format == ReportFormat.Json
            ? JsonSerializer.Serialize(scan.ReportsByRepo, JsonOptions)
            : ReadableCorpusReportWriter.Write(
                [.. scan.ReadableRepos.OrderBy(r => r.Name, StringComparer.Ordinal)],
                [.. scan.MissingRepos.OrderBy(name => name, StringComparer.Ordinal)],
                ReportOutput.ToStyle(validated.Format),
                validated.Verbosity);

        if (!ReportOutput.Emit(content, options.OutputPath, stdout, stderr))
        {
            return 1;
        }

        if (scan.HadMissingRepo || scan.HadUnexpectedFailure)
        {
            return 1;
        }

        return strict && scan.ReportsByRepo.Values.Any(r => ReportOutput.HasCoverageGaps(r.Report)) ? 1 : 0;
    }

    private static async Task<ValidatedOptions> ValidateOptionsAsync(ReportOptions options, TextWriter stderr)
    {
        if (!ReportOutput.TryParseFormat(options.Format, out var reportFormat))
        {
            await stderr.WriteLineAsync(ReportOutput.UnknownFormatMessage(options.Format));
            return new ValidatedOptions(1, default, default, default);
        }

        if (reportFormat == ReportFormat.Sarif)
        {
            await stderr.WriteLineAsync("error: scan-corpus-live does not support --format sarif; run `scan-db` against a single database for a SARIF log.");
            return new ValidatedOptions(1, default, default, default);
        }

        if (!ReportOutput.TryParseConfidence(options.Confidence, out var minimumConfidence))
        {
            await stderr.WriteLineAsync(ReportOutput.UnknownConfidenceMessage(options.Confidence));
            return new ValidatedOptions(1, default, default, default);
        }

        if (!ReportOutput.TryParseVerbosity(options.Verbosity, out var verbosity))
        {
            await stderr.WriteLineAsync(ReportOutput.UnknownVerbosityMessage(options.Verbosity));
            return new ValidatedOptions(1, default, default, default);
        }

        return new ValidatedOptions(null, reportFormat, minimumConfidence, verbosity);
    }

    private static async Task<CorpusScanAccumulator> ScanAllReposAsync(
        CorpusManifest manifest, string clonesRoot, SqlServerOptions sqlOptions, TextWriter stderr, FindingConfidence minimumConfidence, CancellationToken cancellationToken)
    {
        var reportsByRepo = new SortedDictionary<string, CorpusLiveRepoResult>(StringComparer.Ordinal);
        var readableRepos = new List<ReadableCorpusRepo>();
        var missingRepos = new List<string>();
        var hadMissingRepo = false;
        var hadUnexpectedFailure = false;

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

            var outcome = await ScanOneRepoAsync(repo, repoRoot, sqlOptions, stderr, minimumConfidence, cancellationToken);
            if (outcome is null)
            {
                hadUnexpectedFailure = true;
                continue;
            }

            reportsByRepo[repo.Name] = outcome;
            readableRepos.Add(new ReadableCorpusRepo(repo.Name, outcome.Report, repoRoot));
        }

        return new CorpusScanAccumulator(reportsByRepo, readableRepos, missingRepos, hadMissingRepo, hadUnexpectedFailure);
    }

    private sealed record ValidatedOptions(int? ExitCode, ReportFormat Format, FindingConfidence Confidence, ReadableVerbosity Verbosity);

    private sealed record CorpusScanAccumulator(
        SortedDictionary<string, CorpusLiveRepoResult> ReportsByRepo,
        List<ReadableCorpusRepo> ReadableRepos,
        List<string> MissingRepos,
        bool HadMissingRepo,
        bool HadUnexpectedFailure);

    private static async Task<CorpusLiveRepoResult?> ScanOneRepoAsync(
        CorpusRepoEntry repo, string repoRoot, SqlServerOptions sqlOptions, TextWriter stderr, FindingConfidence minimumConfidence,
        CancellationToken cancellationToken = default)
    {
        CorpusLiveRepoResult result;
        try
        {
            result = await CorpusLiveScanRunner.RunAsync(repo, repoRoot, sqlOptions, minimumConfidence, cancellationToken);
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

        foreach (var failure in result.ModuleParseHealth.Files.Where(f => f.Errors.Count > 0))
        {
            await stderr.WriteLineAsync(
                $"warning: '{repo.Name}' module '{failure.Path}' deployed but its own definition failed to reparse " +
                $"({failure.Errors.Count} error(s)) - it contributes zero findings to this scan.");
        }

        foreach (var unanalyzed in result.Report.ParseHealth.Files.SelectMany(f => f.UnanalyzedBatches))
        {
            var what = unanalyzed.ObjectName is { } name ? $"{unanalyzed.Kind} '{name}'" : "an unidentified object";
            await stderr.WriteLineAsync(
                $"warning: '{repo.Name}' {unanalyzed.SourcePath}:{unanalyzed.StartLine} - a batch failed to parse and was dropped; " +
                $"{what} received zero analysis.");
        }

        return result;
    }

    private static string RepoDirectoryName(string url) => url.TrimEnd('/').Split('/')[^1];
}
