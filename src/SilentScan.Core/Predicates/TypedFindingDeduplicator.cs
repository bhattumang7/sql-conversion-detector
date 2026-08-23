namespace SilentScan.Core.Predicates;

public static class TypedFindingDeduplicator
{
    public static IReadOnlyList<TypedPredicateFinding> Dedupe(IReadOnlyList<TypedPredicateFinding> findings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = findings.Where(finding => seen.Add(Key(finding))).ToList();

        return result;
    }

    private static string Key(TypedPredicateFinding finding) =>
        TypedPredicateFindingIdentity.ComputeKey(finding.Column, finding.OtherOperand, finding.Operator);
}
