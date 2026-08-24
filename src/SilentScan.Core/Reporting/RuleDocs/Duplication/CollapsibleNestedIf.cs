using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Duplication;

internal static class CollapsibleNestedIf
{
    public static string RuleId => SarifRuleCatalog.DuplicationRuleId(DuplicationFindingKind.CollapsibleNestedIf);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An IF with no ELSE whose entire body is a single nested IF, also with no ELSE -
            semantically identical to one IF combining both conditions with AND. The extra nesting
            level adds indentation without adding any distinct behavior.
            """,
        HowToFixIt: "Combine the two conditions with AND into a single IF, replacing the nested pair.",
        Examples:
        [
            new RuleDocExample(
                Title: "A nested IF pair that could be one IF",
                NoncompliantSql: """
                    IF @status = 'Active'
                    BEGIN
                        IF @region = 'US'
                        BEGIN
                            SELECT 1;
                        END
                    END
                    """,
                NoncompliantExplanation: "The outer IF's entire body is a single nested IF with no ELSE anywhere - equivalent to one IF combining both conditions with AND.",
                CompliantSql: """
                    IF @status = 'Active' AND @region = 'US'
                    BEGIN
                        SELECT 1;
                    END
                    """,
                CompliantExplanation: "A single IF with an AND-combined condition expresses the same logic with one less nesting level."),
        ]);
}
