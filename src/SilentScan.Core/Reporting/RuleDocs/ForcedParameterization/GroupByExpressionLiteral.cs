using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ForcedParameterization;

internal static class GroupByExpressionLiteral
{
    public static string RuleId => SarifRuleCatalog.ForcedParameterizationRuleId(ForcedParameterizationFindingKind.GroupByExpressionLiteral);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Reported only when the target database has `PARAMETERIZATION FORCED` on, and only for
            a literal inside a `GROUP BY` expression - confirmed directly against a real engine:
            `WHERE Val > 5 GROUP BY (Id + 1)` parameterizes the `WHERE`-clause equality but keeps
            the `1` in the `GROUP BY` expression untouched, in the same cached plan.

            Unlike the `ORDER BY` sibling rule, there is no bare-literal ordinal idiom to exclude
            here: `GROUP BY 1` is not a valid ordinal position reference in T-SQL at all - the
            engine rejects it outright ("Each GROUP BY expression must contain at least one column
            that is not an outer reference"), confirmed directly. Every literal-bearing GROUP BY
            expression is this one shape.
            """,
        HowToFixIt: """
            Pass the literal inside the GROUP BY expression as a parameter or local variable
            instead of a literal.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A literal inside a GROUP BY expression",
                NoncompliantSql: """
                    SELECT OrderId + 1 AS Bucket, COUNT(*) FROM dbo.Orders GROUP BY (OrderId + 1);
                    """,
                NoncompliantExplanation: "Under PARAMETERIZATION FORCED, 1 stays literal in the cached plan - a different bucket offset recompiles instead of reusing this plan.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.GetOrdersByBucket @Offset int AS
                    SELECT OrderId + @Offset AS Bucket, COUNT(*) FROM dbo.Orders GROUP BY (OrderId + @Offset);
                    """,
                CompliantExplanation: "The offset is already a parameter, so every call shares the one compiled plan regardless of PARAMETERIZATION FORCED."),
        ]);
}
