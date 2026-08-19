using System.Text;
using System.Text.Encodings.Web;
using SilentScan.Core.Reporting.RuleDocs;

namespace SilentScan.Core.Reporting;

/// <summary>
/// Renders one rule's own public page (<c>docs/rules/&lt;slug&gt;.html</c>, <see cref="RuleDocSite"/>'s
/// URL scheme), Sonar-shaped: "Why is this an issue" (the rich, multi-paragraph
/// <see cref="RuleDocContent.WhyItMatters"/> when <see cref="RuleDocCatalog"/> has an entry for
/// this rule, else the short <see cref="RuleDefinition.Rationale"/> alone), "How can I fix it"
/// (fix prose plus noncompliant/compliant example pairs), and - only when the catalog names a
/// real, checked-in fixture - "Verified by an automated test", so a reader can tell an
/// illustrative example apart from one this tool's own test suite actually asserts against. No
/// fabricated content anywhere: a rule with no <see cref="RuleDocCatalog"/> entry and no
/// <see cref="RuleDefinition.FixGuidance"/> simply gets the "Why is this an issue" section alone.
/// </summary>
public static class RulePageHtmlWriter
{
    private const string ParagraphEnd = "</p>\n";
    private const string CodeBlockEnd = "</code></pre></div>\n";

    public static string Write(RuleDefinition rule, IReadOnlyList<RuleExample> verifiedExamples)
    {
        RuleDocCatalog.ByRuleId.TryGetValue(rule.Id, out var doc);

        var sb = new StringBuilder();
        AppendDocumentStart(sb, rule);
        AppendWhySection(sb, rule, doc);
        AppendFixSection(sb, rule, doc);
        AppendVerifiedSection(sb, verifiedExamples);
        AppendDocumentEnd(sb);

        return sb.ToString();
    }

    private static void AppendDocumentStart(StringBuilder sb, RuleDefinition rule)
    {
        var encodedId = HtmlEncoder.Default.Encode(rule.Id);
        var encodedTitle = HtmlEncoder.Default.Encode(RuleDocSite.HumanizeTitle(rule.Id));

        sb.Append("""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>
            """);
        sb.Append(encodedTitle).Append(" - SilentScan rules</title>\n<style>\n").Append(RuleDocStyle.Css).Append("\n</style>\n</head>\n<body>\n<main>\n");

        sb.Append("  <a class=\"back-link\" href=\"../rules.html\">&larr; All rules</a>\n");
        sb.Append("  <h1>").Append(encodedTitle).Append("</h1>\n");
        sb.Append("  <p class=\"rule-key\"><code>").Append(encodedId).Append("</code>").Append(ParagraphEnd);
        sb.Append("  <p class=\"tagline\">A medium-confidence or low-confidence finding of this same rule links here too - only the finding's certainty differs, not the underlying issue.").Append(ParagraphEnd);
    }

    private static void AppendWhySection(StringBuilder sb, RuleDefinition rule, RuleDocContent? doc)
    {
        sb.Append("  <h2>Why is this an issue?</h2>\n");
        sb.Append("  <p class=\"rationale\">").Append(HtmlEncoder.Default.Encode(rule.Rationale)).Append(ParagraphEnd);
        if (doc?.WhyItMatters is { } whyItMatters)
        {
            AppendParagraphs(sb, whyItMatters);
        }
    }

    private static void AppendFixSection(StringBuilder sb, RuleDefinition rule, RuleDocContent? doc)
    {
        var hasFixContent = doc?.HowToFixIt is not null || rule.FixGuidance is not null || (doc?.AllExamples.Count ?? 0) > 0;
        if (!hasFixContent)
        {
            return;
        }

        sb.Append("  <h2>How can I fix it?</h2>\n");
        if (doc?.HowToFixIt is { } howToFixIt)
        {
            AppendParagraphs(sb, howToFixIt);
        }
        else if (rule.FixGuidance is { } fixGuidance)
        {
            sb.Append("  <p class=\"fix\">").Append(HtmlEncoder.Default.Encode(fixGuidance)).Append(ParagraphEnd);
        }

        foreach (var example in doc?.AllExamples ?? [])
        {
            AppendAuthoredExample(sb, example);
        }
    }

    private static void AppendAuthoredExample(StringBuilder sb, RuleDocExample example)
    {
        sb.Append("  <h3>").Append(HtmlEncoder.Default.Encode(example.Title)).Append("</h3>\n");
        sb.Append("  <p class=\"example-label fires-label\">Noncompliant code example").Append(ParagraphEnd);
        sb.Append("  <div class=\"fires-block\"><pre><code>").Append(HtmlEncoder.Default.Encode(example.NoncompliantSql.Trim())).Append(CodeBlockEnd);
        AppendExplanation(sb, example.NoncompliantExplanation);

        if (example.CompliantSql is not { } compliantSql)
        {
            return;
        }

        sb.Append("  <p class=\"example-label clean-label\">Compliant solution").Append(ParagraphEnd);
        sb.Append("  <div class=\"clean-block\"><pre><code>").Append(HtmlEncoder.Default.Encode(compliantSql.Trim())).Append(CodeBlockEnd);
        AppendExplanation(sb, example.CompliantExplanation);
    }

    private static void AppendExplanation(StringBuilder sb, string? explanation)
    {
        if (explanation is not null)
        {
            sb.Append("  <p class=\"example-explanation\">").Append(HtmlEncoder.Default.Encode(explanation)).Append(ParagraphEnd);
        }
    }

    private static void AppendVerifiedSection(StringBuilder sb, IReadOnlyList<RuleExample> verifiedExamples)
    {
        if (verifiedExamples.Count == 0)
        {
            return;
        }

        sb.Append("  <h2>Verified by an automated test</h2>\n");
        sb.Append("  <p class=\"example\">This exact pattern also has a real, checked-in regression fixture in this tool's own test suite:").Append(ParagraphEnd);
        foreach (var example in verifiedExamples)
        {
            AppendVerifiedExample(sb, example);
        }
    }

    private static void AppendVerifiedExample(StringBuilder sb, RuleExample example)
    {
        AppendFixturePath(sb, example.FiresPath);
        sb.Append("  <div class=\"fires-block\"><pre><code>").Append(HtmlEncoder.Default.Encode(example.FiresSql.Trim())).Append(CodeBlockEnd);

        if (example.CleanSql is not { } cleanSql)
        {
            return;
        }

        AppendFixturePath(sb, example.CleanPath!);
        sb.Append("  <div class=\"clean-block\"><pre><code>").Append(HtmlEncoder.Default.Encode(cleanSql.Trim())).Append(CodeBlockEnd);
    }

    private static void AppendFixturePath(StringBuilder sb, string path) =>
        sb.Append("  <p class=\"example-path\"><code>").Append(HtmlEncoder.Default.Encode(path)).Append("</code>").Append(ParagraphEnd);

    private static void AppendDocumentEnd(StringBuilder sb) =>
        sb.Append("""
              <footer>
                Generated by <code>silentscan rules-doc</code> from RuleCatalog/RuleDocs - do not hand-edit.
              </footer>
            </main>
            </body>
            </html>
            """);

    /// <summary>Splits a long-form field on blank lines into separate &lt;p&gt; blocks - RuleDocs prose is authored as multi-paragraph triple-quoted text, not one run-on paragraph.</summary>
    private static void AppendParagraphs(StringBuilder sb, string text)
    {
        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var paragraph in paragraphs)
        {
            var normalized = string.Join(' ', paragraph.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            sb.Append("  <p class=\"rationale\">").Append(HtmlEncoder.Default.Encode(normalized)).Append(ParagraphEnd);
        }
    }
}
