using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ForcedParameterization;

internal static class OrderByExpressionLiteral
{
    public static string RuleId => SarifRuleCatalog.ForcedParameterizationRuleId(ForcedParameterizationFindingKind.OrderByExpressionLiteral);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Reported only when the target database has `PARAMETERIZATION FORCED` on, and only for
            a literal nested inside a compound `ORDER BY` expression - confirmed directly against
            a real engine: `ORDER BY (Id + 100)` keeps the `100` literal untouched in the cached
            plan.

            Deliberately excludes a BARE literal as the entire ORDER BY element (`ORDER BY 1`, the
            common ordinal-position idiom) - that shape was not part of this rule's own oracle
            probe, and is structurally different (a small, finite, rarely-varying set of values,
            not a value that scales with the app's own data).
            """,
        HowToFixIt: """
            Pass the literal inside the ORDER BY expression as a parameter or local variable
            instead of a literal.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A literal inside a compound ORDER BY expression",
                NoncompliantSql: """
                    SELECT OrderId FROM dbo.Orders ORDER BY (Priority + 100);
                    """,
                NoncompliantExplanation: "Under PARAMETERIZATION FORCED, 100 stays literal in the cached plan - a different offset recompiles instead of reusing this plan.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.GetOrdersByAdjustedPriority @Offset int AS
                    SELECT OrderId FROM dbo.Orders ORDER BY (Priority + @Offset);
                    """,
                CompliantExplanation: "The offset is already a parameter, so every call shares the one compiled plan regardless of PARAMETERIZATION FORCED."),
        ]);
}
