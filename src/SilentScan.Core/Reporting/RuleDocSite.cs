namespace SilentScan.Core.Reporting;

/// <summary>
/// The public rule-page URL scheme: <c>https://umangbhatt.in/mssql-silentscan/rules/&lt;slug&gt;.html</c>,
/// where a rule ID's <c>silentscan/</c> prefix is dropped and every remaining
/// <c>/</c> becomes <c>-</c> (<c>silentscan/tvf-fence/from-or-join</c> -&gt;
/// <c>rules/tvf-fence-from-or-join.html</c>). A confidence-suffixed SARIF variant
/// (<c>/medium-confidence</c>, <c>/low-confidence</c>) resolves to its base rule's page - the
/// underlying rule is the same one, only the finding's confidence differs. This is the one place
/// that knows the scheme; a golden slug test pins every <see cref="RuleCatalog"/> id so a rule-id
/// rename shows up as a visible diff instead of a silently dead link.
/// </summary>
public static class RuleDocSite
{
    /// <summary>
    /// Where the published rule pages live. Deliberately a hardcoded absolute URL, and the only
    /// one in the codebase: a SARIF <c>helpUri</c> has to resolve from whatever machine reads the
    /// findings, so it cannot be relative, and the index page's links are generated against this
    /// same root. Making it configurable would let the two drift apart - the exact failure this
    /// class exists to prevent.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Major Code Smell", "S1075:URIs should not be hardcoded",
        Justification = "The published site root is a fixed, externally-resolvable fact, not an environment setting - see the comment above. Composing both public URLs from this single constant keeps them from diverging.")]
    private const string SiteRoot = "https://umangbhatt.in/mssql-silentscan";

    public const string IndexUrl = SiteRoot + "/rules.html";

    private const string BaseUrl = SiteRoot + "/rules/";
    private const string SilentScanPrefix = "silentscan/";
    private const string MediumConfidenceSuffix = "/medium-confidence";
    private const string LowConfidenceSuffix = "/low-confidence";

    /// <summary>Strips a confidence suffix, if present, back to the base rule ID it was derived from.</summary>
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

    /// <summary>Path relative to <c>docs/rules.html</c> - what the index page links with, so local preview and the published site both resolve it (unlike <see cref="Url"/>, which is absolute for an external SARIF consumer's <c>helpUri</c>).</summary>
    public static string RelativePath(string ruleId) => $"rules/{Slug(ruleId)}.html";

    /// <summary>Acronyms/initialisms that read wrong in plain Title Case and get force-uppercased after the split.</summary>
    private static readonly HashSet<string> UppercaseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "sql", "udf", "tvf", "fk", "cte", "dml", "orm", "ansi", "ce", "rid", "id",
        "clr", "merge", "top", "like", "isnull", "coalesce", "iif", "having", "waitfor",
    };

    /// <summary>
    /// A readable title mechanically derived from a rule ID's own last path segment - e.g.
    /// <c>silentscan/control-flow/trigger-emits-output</c> -&gt; "Trigger Emits Output". Used
    /// wherever a rule has no hand-authored <c>RuleDocs</c> title (nearly all of them - authoring
    /// 234 titles by hand is its own backlog item), so the index and rule pages never show a raw
    /// slash-delimited ID as the primary label. The raw ID stays visible alongside it (in
    /// <c>&lt;code&gt;</c>) everywhere this is used, the same way Sonar shows both a rule's title
    /// and its rule key.
    /// </summary>
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
