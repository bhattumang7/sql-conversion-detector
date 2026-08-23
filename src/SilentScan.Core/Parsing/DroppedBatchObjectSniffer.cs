using System.Text.RegularExpressions;

namespace SilentScan.Core.Parsing;

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
