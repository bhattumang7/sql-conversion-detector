using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ForcedParameterization;

internal static class ConstantFoldableExpressionLiteral
{
    public static string RuleId => SarifRuleCatalog.ForcedParameterizationRuleId(ForcedParameterizationFindingKind.ConstantFoldableExpressionLiteral);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Reported only when the target database has `PARAMETERIZATION FORCED` on. A real, but
            much milder effect than the rest of this rule family: confirmed directly against a
            real engine, a constant-foldable arithmetic expression against a column (e.g.
            `WHERE Id = 1 + 1008`) parameterizes as TWO separate parameters (`WHERE [Id]=(@1+@2)`)
            instead of the single `@0` a plain `Id = 1009` equality would produce.

            The statement IS still fully parameterized here - there is no per-literal recompile,
            unlike every other member of this family. This is shipped as a low-confidence,
            informational note about a less-optimal parameterization shape, not a plan-cache-bloat
            claim.
            """,
        HowToFixIt: """
            Fold the constant expression yourself before writing the query (`WHERE Id = 1009`
            instead of `WHERE Id = 1 + 1008`) if the extra parameter is undesirable - purely a
            cosmetic/minor optimization, not a correctness or cache-bloat fix.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An unfolded constant arithmetic expression",
                NoncompliantSql: """
                    SELECT Id FROM dbo.T WHERE Id = 1 + 1008;
                    """,
                NoncompliantExplanation: "Parameterizes as two separate parameters (@1 + @2) instead of one folded value - still fully parameterized, just less optimally.",
                CompliantSql: """
                    SELECT Id FROM dbo.T WHERE Id = 1009;
                    """,
                CompliantExplanation: "The already-folded literal parameterizes as a single parameter, the same shape as any other plain equality."),
        ]);
}
