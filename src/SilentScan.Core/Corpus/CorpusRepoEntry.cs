namespace SilentScan.Core.Corpus;

public sealed record CorpusRepoEntry(
    string Name,
    string Url,
    string CommitSha,
    string License,
    IReadOnlyList<string> DdlPaths,
    IReadOnlyList<string> ProcPaths,
    string? DeclaredCollation,
    string? Notes,
    IReadOnlyDictionary<string, string>? TemplateSubstitutions = null,
    string? TempdbCollation = null);
