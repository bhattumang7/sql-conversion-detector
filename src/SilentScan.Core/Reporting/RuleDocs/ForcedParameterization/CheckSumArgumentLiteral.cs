using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ForcedParameterization;

internal static class CheckSumArgumentLiteral
{
    public static string RuleId => SarifRuleCatalog.ForcedParameterizationRuleId(ForcedParameterizationFindingKind.CheckSumArgumentLiteral);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Reported only when the target database has `PARAMETERIZATION FORCED` on. Confirmed
            directly against a real engine: `CHECKSUM('literal')` keeps its literal argument
            untouched in the cached plan while an unrelated literal-vs-literal comparison in the
            same statement still parameterizes.

            A CHECKSUM argument that varies by call gets a fresh compile per distinct value under
            PARAMETERIZATION FORCED.
            """,
        HowToFixIt: """
            Pass the CHECKSUM(...) argument as a parameter or local variable instead of a literal -
            confirmed directly that the engine accepts a variable there.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A literal CHECKSUM argument",
                NoncompliantSql: """
                    SELECT CHECKSUM('some-constant-string');
                    """,
                NoncompliantExplanation: "Under PARAMETERIZATION FORCED, the argument stays literal in the cached plan - a different argument recompiles instead of reusing this plan.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.GetChecksum @Value varchar(200) AS
                    SELECT CHECKSUM(@Value);
                    """,
                CompliantExplanation: "The argument is already a parameter, so every call shares the one compiled plan regardless of PARAMETERIZATION FORCED."),
        ]);
}
