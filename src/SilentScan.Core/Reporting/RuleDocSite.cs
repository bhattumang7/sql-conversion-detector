namespace SilentScan.Core.Reporting;

public static class RuleDocSite
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Major Code Smell", "S1075:URIs should not be hardcoded",
        Justification = "The published site root is a fixed, externally-resolvable fact, not an environment setting - see the comment above. Composing both public URLs from this single constant keeps them from diverging.")]
    private const string SiteRoot = "https://umangbhatt.in/mssql-silentscan";

    public const string IndexUrl = SiteRoot + "/rules.html";

    private const string BaseUrl = SiteRoot + "/rules/";
    private const string SilentScanPrefix = "silentscan/";
    private const string MediumConfidenceSuffix = "/medium-confidence";
    private const string LowConfidenceSuffix = "/low-confidence";

    public static string BaseRuleId(string ruleId)
    {
        if (ruleId.EndsWith(MediumConfidenceSuffix, StringComparison.Ordinal))
        {
            return ruleId[..^MediumConfidenceSuffix.Length];
        }

        if (ruleId.EndsWith(LowConfidenceSuffix, StringComparison.Ordinal))
        {
            return ruleId[..^LowConfidenceSuffix.Length];
        }

        return ruleId;
    }

    public static string Slug(string ruleId)
    {
        var id = BaseRuleId(ruleId);
        var withoutPrefix = id.StartsWith(SilentScanPrefix, StringComparison.Ordinal) ? id[SilentScanPrefix.Length..] : id;
        return withoutPrefix.Replace('/', '-');
    }

    public static string Url(string ruleId) => $"{BaseUrl}{Slug(ruleId)}.html";

    public static string RelativePath(string ruleId) => $"rules/{Slug(ruleId)}.html";

    private static readonly HashSet<string> UppercaseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "sql", "udf", "tvf", "fk", "cte", "dml", "orm", "ansi", "ce", "rid", "id",
        "clr", "merge", "top", "like", "isnull", "coalesce", "iif", "having", "waitfor",
    };

    public static string HumanizeTitle(string ruleId)
    {
        var id = BaseRuleId(ruleId);
        var lastSegment = id[(id.LastIndexOf('/') + 1)..];
        var words = lastSegment.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var titled = words.Select(word => UppercaseWords.Contains(word)
            ? word.ToUpperInvariant()
            : char.ToUpperInvariant(word[0]) + word[1..]);
        return string.Join(' ', titled);
    }
}
