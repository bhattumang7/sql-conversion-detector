using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Formatting;

internal static class TabCharacterUsed
{
    public static string RuleId => SarifRuleCatalog.FormattingRuleId(FormattingFindingKind.TabCharacterUsed);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A literal tab character appears in the source text. Purely a readability signal - no
            query result or execution plan is affected. Tabs render at different widths in different
            editors and diff tools, so alignment that looks correct in one tool can look ragged or
            misleading in another, and a mix of tabs and spaces in the same file makes indentation
            depth ambiguous to a reader.
            """,
        HowToFixIt: """
            Replace the tab character with spaces, and configure the editor to insert spaces for
            indentation going forward.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A tab character used for indentation",
                NoncompliantSql: "SELECT OrderId\n\tFROM dbo.Orders;",
                NoncompliantExplanation: "The FROM clause is indented with a literal tab character, which renders at a different width depending on the viewing tool.",
                CompliantSql: "SELECT OrderId\n    FROM dbo.Orders;",
                CompliantExplanation: "Spaces render identically everywhere, so the indentation looks the same in every editor and diff tool."),
        ]);
}
