namespace SilentScan.Core.Corpus;

/// <summary>One pinned repo entry in corpus/manifest.json (CLAUDE.md corpus rules).</summary>
/// <summary>
/// <paramref name="TemplateSubstitutions"/>: literal text tokens some repos ship DDL with (e.g.
/// DNN Platform's {databaseOwner}/{objectQualifier}) that must be substituted before ScriptDOM
/// can parse the file - a per-repo manifest fact (docs/audit-remediation-plan.md Phase 6.1), not
/// a code change, so adding a new repo with its own template tokens never requires touching
/// <see cref="CorpusTemplatePreprocessor"/>.
/// </summary>
public sealed record CorpusRepoEntry(
    string Name,
    string Url,
    string CommitSha,
    string License,
    IReadOnlyList<string> DdlPaths,
    IReadOnlyList<string> ProcPaths,
    string? DeclaredCollation,
    string? Notes,
    IReadOnlyDictionary<string, string>? TemplateSubstitutions = null);
