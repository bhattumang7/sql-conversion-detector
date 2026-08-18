namespace SilentScan.Core.Reporting;

/// <summary>
/// One real fixture pair for a rule's page: the fires-on SQL the catalog names via
/// <see cref="RuleDefinition.Examples"/>, and - only where the fixture tree genuinely carries
/// one, per CLAUDE.md's <c>RULEID_fires.sql</c>/<c>RULEID_clean.sql</c> convention - the sibling
/// SQL that stays quiet on the identical rule. <see cref="RulePageHtmlWriter"/> stays
/// filesystem-free by design (CLAUDE.md "no network calls in Core"), so the CLI reads both files
/// and hands their text in here.
/// </summary>
public sealed record RuleExample(string FiresPath, string FiresSql, string? CleanPath, string? CleanSql);
