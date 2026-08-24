using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DeprecatedSyntax;

internal static class StringLiteralColumnAlias
{
    public static string RuleId => SarifRuleCatalog.DeprecatedSyntaxRuleId(DeprecatedSyntaxFindingKind.StringLiteralColumnAlias);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A column alias is written as a string literal instead of a real identifier - a deprecated
            aliasing form still accepted by the parser and engine. This form only parses at all
            because of the current QUOTED_IDENTIFIER/session setting, and reads as a string value
            rather than a column name.
            """,
        HowToFixIt: "Alias the column with a real identifier instead of a string literal.",
        Examples:
        [
            new RuleDocExample(
                Title: "A column alias written as a string literal",
                NoncompliantSql: "SELECT OrderId, Status 'OrderStatus' FROM dbo.Orders;",
                NoncompliantExplanation: "'OrderStatus' is written as a string literal alias, a deprecated form that reads as a string value rather than a column name.",
                CompliantSql: "SELECT OrderId, Status AS OrderStatus FROM dbo.Orders;",
                CompliantExplanation: "AS with a real identifier is the current, unambiguous alias syntax."),
        ]);
}
