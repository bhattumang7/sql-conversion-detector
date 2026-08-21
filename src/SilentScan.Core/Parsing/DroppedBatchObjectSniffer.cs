using System.Text.RegularExpressions;

namespace SilentScan.Core.Parsing;

/// <summary>
/// Best-effort read of the object a dropped batch's raw text was defining, for a batch
/// ScriptDOM never turned into an AST at all (see <see cref="UnanalyzedBatch"/>). This is a
/// text-level heuristic, not a parse - it exists only because the real parser already failed on
/// this exact text, so there is nothing more authoritative to consult. Anything short of a
/// confident <c>CREATE</c>/<c>ALTER</c> header match degrades to
/// <see cref="UnanalyzedObjectKind.Unidentified"/> rather than guessing a name or kind - the
/// same "unresolved stays unknown, never guessed" discipline the typed-predicate and lineage
/// passes already follow.
/// </summary>
public static partial class DroppedBatchObjectSniffer
{
    [GeneratedRegex(@"\A(\s|--[^\n]*|/\*.*?\*/)*", RegexOptions.Singleline)]
    private static partial Regex LeadingNoisePattern();

    [GeneratedRegex(
        @"\A(CREATE\s+OR\s+ALTER|CREATE|ALTER)\s+(PROCEDURE|PROC|VIEW|FUNCTION|TRIGGER|TABLE)\s+" +
        @"(?<name>(\[[^\]]+\]|""[^""]+""|\w+)(\s*\.\s*(\[[^\]]+\]|""[^""]+""|\w+))?)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ObjectHeaderPattern();

    public static (UnanalyzedObjectKind Kind, string? ObjectName) Sniff(string batchText)
    {
        var withoutLeadingNoise = StripLeadingNoise(batchText);
        var match = ObjectHeaderPattern().Match(withoutLeadingNoise);
        if (!match.Success)
        {
            return (UnanalyzedObjectKind.Unidentified, null);
        }

        var kind = ToKind(match.Groups[2].Value);
        var name = match.Groups["name"].Value.Trim();
        return kind == UnanalyzedObjectKind.Unidentified || name.Length == 0
            ? (UnanalyzedObjectKind.Unidentified, null)
            : (kind, name);
    }

    private static string StripLeadingNoise(string text)
    {
        var noise = LeadingNoisePattern().Match(text);
        return noise.Success ? text[noise.Length..] : text;
    }

    private static UnanalyzedObjectKind ToKind(string keyword) => keyword.ToUpperInvariant() switch
    {
        "PROCEDURE" or "PROC" => UnanalyzedObjectKind.Procedure,
        "VIEW" => UnanalyzedObjectKind.View,
        "FUNCTION" => UnanalyzedObjectKind.Function,
        "TRIGGER" => UnanalyzedObjectKind.Trigger,
        "TABLE" => UnanalyzedObjectKind.Table,
        _ => UnanalyzedObjectKind.Unidentified,
    };
}
