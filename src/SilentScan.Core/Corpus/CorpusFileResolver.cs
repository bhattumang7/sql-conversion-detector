using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace SilentScan.Core.Corpus;

/// <summary>Resolves a manifest entry's ddlPaths/procPaths globs against a local clone root.</summary>
public static class CorpusFileResolver
{
    public static IReadOnlyList<string> ResolveDdlFiles(CorpusRepoEntry repo, string repoRoot) =>
        Resolve(repo.DdlPaths, repoRoot);

    public static IReadOnlyList<string> ResolveProcFiles(CorpusRepoEntry repo, string repoRoot) =>
        Resolve(repo.ProcPaths, repoRoot);

    /// <summary>All files matched by either ddlPaths or procPaths, deduplicated (some repos declare the same glob for both - CLAUDE.md's DNN/Brent Ozar/Ola Hallengren entries interleave DDL and procs in the same files).</summary>
    public static IReadOnlyList<string> ResolveAllFiles(CorpusRepoEntry repo, string repoRoot) =>
        [.. ResolveDdlFiles(repo, repoRoot).Concat(ResolveProcFiles(repo, repoRoot)).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal)];

    private static IReadOnlyList<string> Resolve(IReadOnlyList<string> globs, string repoRoot)
    {
        if (globs.Count == 0)
        {
            return [];
        }

        var matcher = new Matcher();
        matcher.AddIncludePatterns(globs);

        var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(repoRoot)));
        return [.. result.Files.Select(f => Path.Combine(repoRoot, f.Path)).OrderBy(p => p, StringComparer.Ordinal)];
    }
}
