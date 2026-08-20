using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Identity;

internal static class SeedOrIncrementAnomaly
{
    public static string RuleId => SarifRuleCatalog.IdentityRangeRuleId(IdentityRangeFindingKind.IdentitySeedOrIncrementAnomaly);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `IDENTITY(seed, increment)` almost always appears as `IDENTITY(1,1)` in practice, so a
            negative seed or an increment other than 1 stands out - but unlike the sibling
            range-exhaustion finding, this is not a defect this pass can call one way or the other.
            A negative seed, or an increment of 2, -1, or some other non-1 value, is a real,
            deliberate design choice in more than one legitimate scheme: a reversed-numbering scheme
            that counts down from a high seed, an increment of 2 used to interleave two independent
            writers' own ranges without ever colliding (one writer seeded even, the other odd), or a
            negative increment supporting a "most recently inserted sorts first" ordering that
            piggybacks on the clustering key instead of needing an extra ORDER BY.

            Because it's genuinely ambiguous between "deliberate design" and "oversight" - and
            because it's a SCHEMA fact rather than a data-state one (it's identical whether read from
            a development copy of the schema or a production one, since it comes straight from the
            `CREATE TABLE ... IDENTITY(seed, increment)` declaration itself, never live data), this
            finding is reported at Low confidence and worded as an informational data-modeling
            signal worth a second look, not as a claim that something is wrong.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An identity column with a negative seed and a non-1 increment",
                NoncompliantSql: """
                    CREATE TABLE dbo.LegacyImportBatches
                    (
                        BatchId INT IDENTITY(-1, -1) PRIMARY KEY,
                        Source  VARCHAR(50) NOT NULL
                    );
                    """,
                NoncompliantExplanation: "A seed of -1 and an increment of -1 both deviate from the default IDENTITY(1,1) - this might be a deliberate reversed-numbering scheme (a real, legitimate pattern), or it might be an oversight from an accidentally-transposed IDENTITY argument order; the schema alone can't tell which, so this is flagged for review rather than asserted as wrong."),
        ]);
}
