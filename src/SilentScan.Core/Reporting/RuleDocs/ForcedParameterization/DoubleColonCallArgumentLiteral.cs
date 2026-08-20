using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ForcedParameterization;

internal static class DoubleColonCallArgumentLiteral
{
    public static string RuleId => SarifRuleCatalog.ForcedParameterizationRuleId(ForcedParameterizationFindingKind.DoubleColonCallArgumentLiteral);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Reported only when the target database has `PARAMETERIZATION FORCED` on. Confirmed
            directly against a real engine: a literal argument to a `TypeName::Method(...)`
            static call (CLR user-defined types - `geography::Parse('POINT(1 1)')` and similar)
            stays untouched in the cached plan while an unrelated literal-vs-literal comparison in
            the same statement still parameterizes.

            A spatial or hierarchyid literal built this way (a point, a well-known-text string) is
            typically constant per call site in application code, but when it does vary - a
            geofencing query built per request, for example - this exclusion means it never
            benefits from PARAMETERIZATION FORCED the way an ordinary WHERE-clause literal would.
            """,
        HowToFixIt: """
            Pass the static-call argument as a parameter or local variable instead of a literal.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A literal argument to a static CLR type method",
                NoncompliantSql: """
                    SELECT geography::Parse('POINT(1 1)').STAsText();
                    """,
                NoncompliantExplanation: "Under PARAMETERIZATION FORCED, the well-known-text literal stays untouched in the cached plan - a different point recompiles instead of reusing this plan.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.DescribePoint @Wkt varchar(200) AS
                    SELECT geography::Parse(@Wkt).STAsText();
                    """,
                CompliantExplanation: "The argument is already a parameter, so every call shares the one compiled plan regardless of PARAMETERIZATION FORCED."),
        ]);
}
