using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DeprecatedSyntax;

internal static class NonAnsiComparisonOperator
{
    public static string RuleId => SarifRuleCatalog.DeprecatedSyntaxRuleId(DeprecatedSyntaxFindingKind.NonAnsiComparisonOperator);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A non-ANSI comparison operator (!=, !<, !>) is used instead of the ANSI-standard
            spelling. These operators are T-SQL-specific and unfamiliar to anyone coming from another
            database engine or reading ANSI-standard SQL.
            """,
        HowToFixIt: """
            Use the ANSI-standard comparison operator (e.g. <> instead of !=) instead of the non-ANSI
            spelling.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "The T-SQL-specific != operator",
                NoncompliantSql: "SELECT OrderId FROM dbo.Orders WHERE Status != 'Cancelled';",
                NoncompliantExplanation: "!= is a T-SQL-specific spelling not recognized as standard ANSI SQL syntax.",
                CompliantSql: "SELECT OrderId FROM dbo.Orders WHERE Status <> 'Cancelled';",
                CompliantExplanation: "<> is the ANSI-standard spelling for the same comparison."),
        ]);
}
