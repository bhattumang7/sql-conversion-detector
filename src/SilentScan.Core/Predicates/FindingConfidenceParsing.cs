namespace SilentScan.Core.Predicates;

public static class FindingConfidenceParsing
{
    public const string OptionDescription =
        "The least confident a finding may be and still be reported: high (default - only findings resting on real, provably-constant source text or a provable structural/plan-shape fact), medium (also includes a dynamic-SQL finding derived from a value this scan could not prove constant, e.g. a symbolic placeholder standing in for an uninitialized or caller-unknown variable, plus a few findings that can be a genuine deliberate choice rather than a bug), or low (also includes findings that are real but carry no magnitude claim, e.g. a predicate against a value the optimizer's cardinality estimate is provably blind to).";

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
