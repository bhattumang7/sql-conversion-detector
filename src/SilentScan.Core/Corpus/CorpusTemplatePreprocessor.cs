namespace SilentScan.Core.Corpus;

/// <summary>
/// Some corpus repos ship DDL with text-template placeholders that must be substituted with
/// their conventional values before ScriptDOM can parse the file - a documented preprocessing
/// step, not a dialect failure. The substitution map itself lives per repo in
/// corpus/manifest.json's <c>templateSubstitutions</c> (docs/audit-remediation-plan.md Phase
/// 6.1) rather than hardcoded here by repo name, so a new repo with its own template tokens is
/// a manifest edit, not a code change.
/// </summary>
public static class CorpusTemplatePreprocessor
{
    public static string Apply(IReadOnlyDictionary<string, string>? substitutions, string sql)
    {
        if (substitutions is not { Count: > 0 })
        {
            return sql;
        }

        foreach (var (token, replacement) in substitutions)
        {
            sql = sql.Replace(token, replacement, StringComparison.Ordinal);
        }

        return sql;
    }
}
