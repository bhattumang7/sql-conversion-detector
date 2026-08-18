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
    public static string Write(RuleDefinition rule, IReadOnlyList<RuleExample> verifiedExamples)
    {
        RuleDocCatalog.ByRuleId.TryGetValue(rule.Id, out var doc);

        var sb = new StringBuilder();
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
        sb.Append("  <p class=\"rule-key\"><code>").Append(encodedId).Append("</code></p>\n");
        sb.Append("  <p class=\"tagline\">A medium-confidence or low-confidence finding of this same rule links here too - only the finding's certainty differs, not the underlying issue.</p>\n");

        sb.Append("  <h2>Why is this an issue?</h2>\n");
        sb.Append("  <p class=\"rationale\">").Append(HtmlEncoder.Default.Encode(rule.Rationale)).Append("</p>\n");
        if (doc?.WhyItMatters is { } whyItMatters)
        {
            AppendParagraphs(sb, whyItMatters);
        }

        var hasFixContent = doc?.HowToFixIt is not null || rule.FixGuidance is not null || (doc?.AllExamples.Count ?? 0) > 0;
        if (hasFixContent)
        {
            sb.Append("  <h2>How can I fix it?</h2>\n");
            if (doc?.HowToFixIt is { } howToFixIt)
            {
                AppendParagraphs(sb, howToFixIt);
            }
            else if (rule.FixGuidance is { } fixGuidance)
            {
                sb.Append("  <p class=\"fix\">").Append(HtmlEncoder.Default.Encode(fixGuidance)).Append("</p>\n");
            }

            foreach (var example in doc?.AllExamples ?? [])
            {
                sb.Append("  <h3>").Append(HtmlEncoder.Default.Encode(example.Title)).Append("</h3>\n");
                sb.Append("  <p class=\"example-label fires-label\">Noncompliant code example</p>\n");
                sb.Append("  <div class=\"fires-block\"><pre><code>").Append(HtmlEncoder.Default.Encode(example.NoncompliantSql.Trim())).Append("</code></pre></div>\n");
                if (example.NoncompliantExplanation is { } noncompliantExplanation)
                {
                    sb.Append("  <p class=\"example-explanation\">").Append(HtmlEncoder.Default.Encode(noncompliantExplanation)).Append("</p>\n");
                }

                if (example.CompliantSql is { } compliantSql)
                {
                    sb.Append("  <p class=\"example-label clean-label\">Compliant solution</p>\n");
                    sb.Append("  <div class=\"clean-block\"><pre><code>").Append(HtmlEncoder.Default.Encode(compliantSql.Trim())).Append("</code></pre></div>\n");
                    if (example.CompliantExplanation is { } compliantExplanation)
                    {
                        sb.Append("  <p class=\"example-explanation\">").Append(HtmlEncoder.Default.Encode(compliantExplanation)).Append("</p>\n");
                    }
                }
            }
        }

        if (verifiedExamples.Count > 0)
        {
            sb.Append("  <h2>Verified by an automated test</h2>\n");
            sb.Append("  <p class=\"example\">This exact pattern also has a real, checked-in regression fixture in this tool's own test suite:</p>\n");
            foreach (var example in verifiedExamples)
            {
                sb.Append("  <p class=\"example-path\"><code>").Append(HtmlEncoder.Default.Encode(example.FiresPath)).Append("</code></p>\n");
                sb.Append("  <div class=\"fires-block\"><pre><code>").Append(HtmlEncoder.Default.Encode(example.FiresSql.Trim())).Append("</code></pre></div>\n");

                if (example.CleanSql is { } cleanSql)
                {
                    sb.Append("  <p class=\"example-path\"><code>").Append(HtmlEncoder.Default.Encode(example.CleanPath!)).Append("</code></p>\n");
                    sb.Append("  <div class=\"clean-block\"><pre><code>").Append(HtmlEncoder.Default.Encode(cleanSql.Trim())).Append("</code></pre></div>\n");
                }
            }
        }

        sb.Append("""
              <footer>
                Generated by <code>silentscan rules-doc</code> from RuleCatalog/RuleDocs - do not hand-edit.
              </footer>
            </main>
            </body>
            </html>
            """);

        return sb.ToString();
    }

    /// <summary>Splits a long-form field on blank lines into separate &lt;p&gt; blocks - RuleDocs prose is authored as multi-paragraph triple-quoted text, not one run-on paragraph.</summary>
    private static void AppendParagraphs(StringBuilder sb, string text)
    {
        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var paragraph in paragraphs)
        {
            var normalized = string.Join(' ', paragraph.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            sb.Append("  <p class=\"rationale\">").Append(HtmlEncoder.Default.Encode(normalized)).Append("</p>\n");
        }
    }
}
