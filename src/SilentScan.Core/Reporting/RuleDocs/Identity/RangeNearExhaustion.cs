using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Identity;

internal static class RangeNearExhaustion
{
    public static string RuleId => SarifRuleCatalog.IdentityRangeRuleId(IdentityRangeFindingKind.IdentityRangeNearExhaustion);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An `IDENTITY` column's current value is bounded above (or below, for a descending
            identity) by the maximum value its own declared type can represent - a `TINYINT`
            identity tops out at 255, an `INT` at roughly 2.1 billion, and once that ceiling is hit
            there is no graceful degradation: every subsequent `INSERT` raises a hard
            arithmetic-overflow error (Msg 8115) until the column is widened or reseeded. This
            finding fires when a column's current value has consumed 90% or more of its type's
            representable range in the direction it's actually incrementing, so there's real runway
            left to act before the column locks up entirely.

            This is a genuinely DATA-STATE fact, not a schema one - it depends on how many rows have
            actually been inserted over the table's lifetime, which is meaningless to ask against a
            low-value development database (an identity sitting at 400 proves nothing about whether
            production is close to exhausted) and only meaningful against a real, production-shaped
            target. For that reason this rule only ever fires a warning; it never reports a passing
            "identity range OK" state, since the absence of a finding on a low-value dev database
            would prove nothing about production - reporting a clean state there would be exactly the
            kind of false reassurance CLAUDE.md's "never report a clean/passing state as evidence"
            rule for data-state-decidable checks exists to prevent. Every finding's own detail text
            restates that precondition explicitly.
            """,
        HowToFixIt: """
            Widen the IDENTITY column's declared type before its range is exhausted (e.g. INT to
            BIGINT), or reseed it into unused range if the column's real domain allows that safely.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A TINYINT identity column close to its type's maximum",
                NoncompliantSql: """
                    CREATE TABLE dbo.StatusCodes
                    (
                        Id   TINYINT IDENTITY(0,1) PRIMARY KEY,
                        Name VARCHAR(50) NOT NULL
                    );
                    -- Current identity value in production: 250 (TINYINT's max is 255)
                    """,
                NoncompliantExplanation: "250 out of a maximum representable value of 255 is over 90% consumed - only 5 more inserts remain before every subsequent INSERT raises an arithmetic-overflow error.",
                CompliantSql: """
                    CREATE TABLE dbo.StatusCodes_New
                    (
                        Id   INT IDENTITY(250,1) PRIMARY KEY,
                        Name VARCHAR(50) NOT NULL
                    );
                    -- Migrate existing rows, then swap StatusCodes_New into StatusCodes's place -
                    -- SQL Server does not allow ALTER COLUMN to change an IDENTITY column's own
                    -- declared type in place, so widening requires rebuilding the table.
                    """,
                CompliantExplanation: "Recreating the column as INT (seeded to continue from the current value) restores enormous headroom before the same exhaustion risk could recur."),
        ]);
}
