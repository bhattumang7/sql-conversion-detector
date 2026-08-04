namespace SilentScan.Core.Predicates;

/// <summary>
/// The <c>--confidence</c> flag's parsing, shared by every CLI entry point that lets a caller
/// widen a scan/verify past High (<c>SilentScan.Cli</c>'s scan-db/scan-corpus-live,
/// <c>SilentScan.Verify</c>'s verify-corpus) - kept in Core rather than duplicated per project
/// since both already depend on Core and the string-to-enum mapping is exactly the same choice
/// either way: get it wrong in one place and both tools drift out of sync on what "medium" means.
/// </summary>
public static class FindingConfidenceParsing
{
    public const string OptionDescription =
        "The least confident a finding may be and still be reported: high (default - only findings resting on real, provably-constant source text) or medium (also includes a dynamic-SQL finding derived from a value this scan could not prove constant, e.g. a symbolic placeholder standing in for an uninitialized or caller-unknown variable). Low is not yet produced by anything in this tool.";

    public static bool TryParse(string confidence, out FindingConfidence parsed)
    {
        switch (confidence)
        {
            case "high":
                parsed = FindingConfidence.High;
                return true;
            case "medium":
                parsed = FindingConfidence.Medium;
                return true;
            case "low":
                parsed = FindingConfidence.Low;
                return true;
            default:
                parsed = FindingConfidence.High;
                return false;
        }
    }

    public static string UnknownConfidenceMessage(string confidence) =>
        $"error: unknown --confidence '{confidence}' (expected 'high', 'medium' or 'low')";
}
