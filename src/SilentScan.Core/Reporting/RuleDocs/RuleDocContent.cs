namespace SilentScan.Core.Reporting.RuleDocs;

/// <summary>
/// One example on a rule's own page: a title identifying the specific shape (a rule can have
/// several - different call sites, different rewrite outcomes), the noncompliant SQL, and -
/// where the rule has one - the compliant rewrite, each with its own short explanation of what's
/// happening. Mirrors Sonar's "Noncompliant code example" / "Compliant solution" pairing; a rule
/// with no rewrite at all (e.g. a pattern that's simply always wrong, nothing to become instead)
/// carries <see cref="CompliantSql"/> as null and the page omits that half rather than inventing
/// one.
/// </summary>
public sealed record RuleDocExample(string Title, string NoncompliantSql, string? NoncompliantExplanation = null, string? CompliantSql = null, string? CompliantExplanation = null);

/// <summary>
/// One rule's full-length public-page content - as long as the rule genuinely needs, since it
/// lives in its own file (see e.g. <c>RuleDocs/Tier1/FunctionWrappedColumn.cs</c>) rather than a
/// positional-record argument. <see cref="WhyItMatters"/> can run several paragraphs; the short
/// one-liner in <see cref="RuleCatalog"/> stays what SARIF's <c>shortDescription</c> and the
/// index-page snippet use, since those genuinely need to stay short. <see cref="HowToFixIt"/> is
/// the fix explanation prose (the same one-sentence <see cref="RuleDefinition.FixGuidance"/>
/// still feeds SARIF's own fix-guidance slot, but the page reads this instead when it's set).
/// Null/empty fields render no section - never a fabricated one.
/// </summary>
public sealed record RuleDocContent(string WhyItMatters, string? HowToFixIt = null, IReadOnlyList<RuleDocExample>? Examples = null)
{
    public IReadOnlyList<RuleDocExample> AllExamples => Examples ?? [];
}
