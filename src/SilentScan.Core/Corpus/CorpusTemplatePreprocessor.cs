namespace SilentScan.Core.Corpus;

/// <summary>
/// Some corpus repos ship DDL with text-template placeholders that must be substituted with
/// their conventional values before ScriptDOM can parse the file - a documented preprocessing
/// step, not a dialect failure. Applied per repo by manifest entry name; see each repo's
/// manifest.json "notes" field for why.
/// </summary>
public static class CorpusTemplatePreprocessor
{
    public static string Apply(string repoName, string sql) => repoName switch
    {
        // DNN Platform's *.SqlDataProvider files use {databaseOwner}/{objectQualifier}
        // tokens, conventionally substituted with "dbo." and "" respectively - see
        // corpus/manifest.json's dnn-platform entry.
        "dnn-platform" => sql.Replace("{databaseOwner}", "dbo.", StringComparison.Ordinal)
                             .Replace("{objectQualifier}", string.Empty, StringComparison.Ordinal),
        _ => sql,
    };
}
