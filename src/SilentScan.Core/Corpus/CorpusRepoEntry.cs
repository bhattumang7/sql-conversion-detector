namespace SilentScan.Core.Corpus;

/// <summary>One pinned repo entry in corpus/manifest.json (CLAUDE.md corpus rules).</summary>
/// <summary>
/// <paramref name="TemplateSubstitutions"/>: literal text tokens some repos ship DDL with (e.g.
/// DNN Platform's {databaseOwner}/{objectQualifier}) that must be substituted before ScriptDOM
/// can parse the file - a per-repo manifest fact (docs/audit-remediation-plan.md Phase 6.1), not
/// a code change, so adding a new repo with its own template tokens never requires touching
/// <see cref="CorpusTemplatePreprocessor"/>. <paramref name="TempdbCollation"/> is tempdb's own
/// server-level collation, when the manifest states it - distinct from
/// <paramref name="DeclaredCollation"/> (the scanned USER database's collation), since a real SQL
/// Server instance's tempdb collation is fixed at install time and frequently differs. Null (the
/// default, and the overwhelming majority of entries) means "not stated" - a #temp table/table
/// variable then falls back to <paramref name="DeclaredCollation"/>, exactly like before this
/// field existed.
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
    IReadOnlyDictionary<string, string>? TemplateSubstitutions = null,
    string? TempdbCollation = null);
