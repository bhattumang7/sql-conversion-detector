using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace SilentScan.Core.Corpus;

public static class CorpusFileResolver
{
    public static IReadOnlyList<string> ResolveDdlFiles(CorpusRepoEntry repo, string repoRoot) =>
        Resolve(repo.DdlPaths, repoRoot);

    public static IReadOnlyList<string> ResolveProcFiles(CorpusRepoEntry repo, string repoRoot) =>
        Resolve(repo.ProcPaths, repoRoot);

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
