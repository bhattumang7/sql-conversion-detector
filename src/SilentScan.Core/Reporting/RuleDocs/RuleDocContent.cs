namespace SilentScan.Core.Reporting.RuleDocs;

public sealed record RuleDocExample(string Title, string NoncompliantSql, string? NoncompliantExplanation = null, string? CompliantSql = null, string? CompliantExplanation = null);

public sealed record RuleDocContent(string WhyItMatters, string? HowToFixIt = null, IReadOnlyList<RuleDocExample>? Examples = null)
{
    public IReadOnlyList<RuleDocExample> AllExamples => Examples ?? [];
}
