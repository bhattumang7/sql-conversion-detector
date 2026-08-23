namespace SilentScan.Core.Corpus;

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
