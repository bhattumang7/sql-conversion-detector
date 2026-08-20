using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ControlFlow;

internal static class NonDeterministicCaseInput
{
    public static string RuleId => SarifRuleCatalog.ControlFlowRiskRuleId(ControlFlowRiskFindingKind.NonDeterministicCaseInput);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A non-deterministic function (`NEWID()`, `RAND()`, `CRYPT_GEN_RANDOM()`) used as a
            simple CASE expression's own INPUT expression is a genuinely surprising trap,
            oracle-confirmed directly against a real compiled plan: the optimizer rewrites `CASE
            NEWID() WHEN v1 THEN r1 WHEN v2 THEN r2 ELSE r3 END` into a NESTED `CASE WHEN NEWID()=v1
            THEN r1 ELSE CASE WHEN NEWID()=v2 THEN r2 ELSE r3 END END` - three SEPARATE intrinsic
            call sites in the real scalar-operator tree, not one value evaluated once and reused
            across the comparisons. This was confirmed as a genuine per-call re-evaluation, not
            merely a repeated textual reference to one cached value: three bare `RAND()` references
            in a single real executed SELECT list independently returned three different values.

            The practical consequence is severe: for a large-domain function like `NEWID()` or
            `CRYPT_GEN_RANDOM()`, every WHEN branch becomes, in effect, permanently unreachable dead
            code - the odds of one freshly-generated random value matching a fixed literal are
            astronomically small - so the whole CASE structure silently always evaluates to its ELSE
            (or NULL, if it has none, compounding with the sibling missing-ELSE finding when both
            apply to the same expression). This is a structurally different claim from a separate,
            already-investigated item about a non-foldable nondeterministic intrinsic in a WHERE
            predicate's seek/scan behavior - that one was checked and found NOT to hold; this one, a
            CASE expression re-evaluating its own input, was independently oracle-confirmed true.
            """,
        HowToFixIt: """
            Compute the non-deterministic value once into a variable before the CASE expression, and
            use that variable as the CASE input instead of calling the function directly inside it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "NEWID() as a simple CASE expression's input",
                NoncompliantSql: """
                    SELECT
                        CASE NEWID()
                            WHEN @KnownGuid1 THEN 'MatchedFirst'
                            WHEN @KnownGuid2 THEN 'MatchedSecond'
                            ELSE 'NoMatch'
                        END AS Result;
                    """,
                NoncompliantExplanation: "The optimizer evaluates NEWID() separately for each WHEN comparison, not once - so this CASE effectively always falls through to ELSE 'NoMatch', since a freshly-generated GUID matching a fixed literal is astronomically unlikely on any given evaluation.",
                CompliantSql: """
                    DECLARE @Id UNIQUEIDENTIFIER = NEWID();
                    SELECT
                        CASE @Id
                            WHEN @KnownGuid1 THEN 'MatchedFirst'
                            WHEN @KnownGuid2 THEN 'MatchedSecond'
                            ELSE 'NoMatch'
                        END AS Result;
                    """,
                CompliantExplanation: "NEWID() is called exactly once into @Id, and the same fixed value is compared against every WHEN branch, restoring the behavior a CASE expression is normally expected to have."),
        ]);
}
