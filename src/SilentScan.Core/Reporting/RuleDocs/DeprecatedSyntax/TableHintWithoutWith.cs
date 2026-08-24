using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DeprecatedSyntax;

internal static class TableHintWithoutWith
{
    public static string RuleId => SarifRuleCatalog.DeprecatedSyntaxRuleId(DeprecatedSyntaxFindingKind.TableHintWithoutWith);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A table hint is written without the WITH keyword - a deprecated syntax form still
            accepted by the parser and engine. Microsoft documents this old, unbracketed hint syntax
            as scheduled for removal in a future release.
            """,
        HowToFixIt: "Add the WITH keyword before the table hint.",
        Examples:
        [
            new RuleDocExample(
                Title: "A table hint missing the WITH keyword",
                NoncompliantSql: "SELECT OrderId FROM dbo.Orders (NOLOCK) WHERE Status = 'Active';",
                NoncompliantExplanation: "The hint is written without WITH, a deprecated form Microsoft documents as scheduled for removal.",
                CompliantSql: "SELECT OrderId FROM dbo.Orders WITH (NOLOCK) WHERE Status = 'Active';",
                CompliantExplanation: "The WITH keyword makes this the current, non-deprecated hint syntax."),
        ]);
}
