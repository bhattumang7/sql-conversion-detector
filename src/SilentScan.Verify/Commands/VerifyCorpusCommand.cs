using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using SilentScan.Core.Corpus;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Verify.Commands;

/// <summary>
/// `silentscan-verify verify-corpus` — the formal Verify pass for corpus findings (CLAUDE.md
/// Verification workflow): deploys each repo's own DDL to a disposable database, then
/// oracle-probes every SCAN_FORCED/RANGE_SEEK finding for that repo via CONVERT_IMPLICIT in
/// plan XML, rather than trusting the static classifier alone. Only ddlPaths are deployed
/// (never procPaths) - findings always resolve to base-table columns (CLAUDE.md Pass 3), so
/// the tables alone are sufficient, and this keeps deployment scoped to schema, never the
/// repo's own procedural logic.
/// </summary>
public static class VerifyCorpusCommand
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
            Description = "Directory containing each repo's local clone.",
            DefaultValueFactory = _ => "corpus/_clones",
        };

        var repoOption = new Option<string?>("--repo")
        {
            Description = "Only verify the manifest entry with this name (default: all repos).",
        };

        var command = new Command("verify-corpus", "Oracle-confirm ScanForced/RangeSeek findings for each corpus repo against a disposable SQL Server database.")
        {
            manifestOption,
            clonesRootOption,
            repoOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var manifestPath = parseResult.GetValue(manifestOption)!;
            var clonesRoot = parseResult.GetValue(clonesRootOption)!;
            var repoFilter = parseResult.GetValue(repoOption);
            return await RunAsync(manifestPath, clonesRoot, repoFilter, SqlServerOptions.LocalDocker, Console.Out, Console.Error, cancellationToken);
        });

        return command;
    }

    internal static async Task<int> RunAsync(
        string manifestPath,
        string clonesRoot,
        string? repoFilter,
        SqlServerOptions sqlOptions,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            await stderr.WriteLineAsync($"error: manifest not found: {manifestPath}");
            return 1;
        }

        var manifest = CorpusManifestLoader.Load(manifestPath);
        var repos = manifest.Repos.Where(r => repoFilter is null || string.Equals(r.Name, repoFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (repos.Count == 0)
        {
            await stderr.WriteLineAsync($"error: no manifest entry matches --repo '{repoFilter}'.");
            return 1;
        }

        var context = new VerifyContext(new DatabaseProvisioner(sqlOptions), new CorpusFindingVerifier(sqlOptions), sqlOptions);
        var summaries = new SortedDictionary<string, RepoVerificationSummary>(StringComparer.Ordinal);
        var hadMissingRepo = false;

        foreach (var repo in repos)
        {
            var repoRoot = Path.Combine(clonesRoot, RepoDirectoryName(repo.Url));
            if (!Directory.Exists(repoRoot))
            {
                await stderr.WriteLineAsync($"warning: '{repo.Name}' has no local clone at {repoRoot} - skipped.");
                hadMissingRepo = true;
                continue;
            }

            await stderr.WriteLineAsync($"Verifying {repo.Name}...");
            summaries[repo.Name] = await VerifyRepoAsync(repo, repoRoot, context, cancellationToken);
        }

        await stdout.WriteLineAsync(JsonSerializer.Serialize(summaries, JsonOptions));

        return hadMissingRepo ? 1 : 0;
    }

    private sealed record VerifyContext(DatabaseProvisioner Provisioner, CorpusFindingVerifier Verifier, SqlServerOptions SqlOptions);

    private static async Task<RepoVerificationSummary> VerifyRepoAsync(
        CorpusRepoEntry repo,
        string repoRoot,
        VerifyContext context,
        CancellationToken cancellationToken)
    {
        var allFiles = CorpusFileResolver.ResolveAllFiles(repo, repoRoot);
        var parseResults = new List<SqlParseResult>(allFiles.Count);
        foreach (var file in allFiles)
        {
            parseResults.Add(await ParseCorpusFileAsync(repo, file, cancellationToken));
        }

        var report = ScanReportBuilder.BuildFromParseResults(parseResults, repo.DeclaredCollation);
        var probeWorthy = report.TypedFindings.Where(f => f.Verdict is Verdict.ScanForced or Verdict.RangeSeek).ToList();

        var databaseName = SanitizeDatabaseName(repo.Name);
        var deploymentErrors = new List<string>();

        await context.Provisioner.CreateFreshAsync(databaseName, cancellationToken);
        try
        {
            var ddlFiles = CorpusFileResolver.ResolveDdlFiles(repo, repoRoot);
            var deployer = new ScriptDeployer(context.SqlOptions);
            foreach (var ddlFile in ddlFiles)
            {
                try
                {
                    var text = CorpusTemplatePreprocessor.Apply(repo.Name, await File.ReadAllTextAsync(ddlFile, cancellationToken));
                    await deployer.DeployAsync(text, databaseName, cancellationToken);
                }
                catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
                {
                    // Real-world corpus DDL routinely contains statements our disposable oracle
                    // can't execute (permission grants, filegroup references, ordering
                    // dependencies across files) - deployment is best-effort per file so one
                    // bad file doesn't sink every other table's probes.
                    deploymentErrors.Add($"{ddlFile}: {ex.Message}");
                }
            }

            var results = new List<CorpusFindingResult>();
            foreach (var finding in probeWorthy)
            {
                results.Add(await context.Verifier.VerifyAsync(databaseName, finding, cancellationToken));
            }

            return new RepoVerificationSummary(
                TotalDdlFiles: ddlFiles.Count,
                DeploymentErrors: deploymentErrors,
                ProbeWorthyFindingCount: probeWorthy.Count,
                Confirmed: [.. results.Where(r => r.Outcome == CorpusFindingOutcome.Confirmed)],
                NotConfirmed: [.. results.Where(r => r.Outcome == CorpusFindingOutcome.NotConfirmed)],
                NotProbeable: [.. results.Where(r => r.Outcome == CorpusFindingOutcome.NotProbeable)],
                ProbeFailed: [.. results.Where(r => r.Outcome == CorpusFindingOutcome.ProbeFailed)],
                DynamicSql: DynamicSqlSummary.From(report.DynamicSqlFindings));
        }
        finally
        {
            await context.Provisioner.DropIfExistsAsync(databaseName, cancellationToken);
        }
    }

    private static async Task<SqlParseResult> ParseCorpusFileAsync(CorpusRepoEntry repo, string path, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        text = CorpusTemplatePreprocessor.Apply(repo.Name, text);
        return SqlScriptParser.ParseText(path, text);
    }

    private static string RepoDirectoryName(string url) => url.TrimEnd('/').Split('/')[^1];

    private static string SanitizeDatabaseName(string repoName)
    {
        var sanitized = new string([.. repoName.Select(c => char.IsLetterOrDigit(c) ? c : '_')]);
        return $"SilentScanCorpus_{sanitized}";
    }
}

/// <summary>Per-repo oracle-confirmation outcome (CLAUDE.md: "Study reports only oracle-confirmed findings; static-only findings go in an appendix"), plus how much of the repo's dynamic SQL could be analyzed at all (CLAUDE.md dynamic SQL policy: "X% of procs contain dynamic SQL we could not analyze" - reported, never silently dropped).</summary>
public sealed record RepoVerificationSummary(
    int TotalDdlFiles,
    IReadOnlyList<string> DeploymentErrors,
    int ProbeWorthyFindingCount,
    IReadOnlyList<CorpusFindingResult> Confirmed,
    IReadOnlyList<CorpusFindingResult> NotConfirmed,
    IReadOnlyList<CorpusFindingResult> NotProbeable,
    IReadOnlyList<CorpusFindingResult> ProbeFailed,
    DynamicSqlSummary DynamicSql);
