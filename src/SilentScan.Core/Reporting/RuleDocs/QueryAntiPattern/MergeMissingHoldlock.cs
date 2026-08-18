using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class MergeMissingHoldlock
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.MergeMissingHoldlock);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            MERGE's WHEN NOT MATCHED THEN INSERT branch fires when the target row a source row is
            supposed to match doesn't yet exist. Under the default READ COMMITTED isolation level,
            the read that decides "does a matching target row exist" and the write that inserts a
            new one aren't protected against another session doing the exact same thing
            concurrently, and this is a documented, well-known MERGE race, not a theoretical
            corner case: two sessions running the same MERGE statement against the same target key
            at close to the same time can both evaluate the matching condition, both see no
            matching row (because neither session's insert has committed yet, so neither is visible
            to the other), and both proceed to the WHEN NOT MATCHED branch and attempt to insert.
            Walk it through concretely - session A runs MERGE targeting key 42: it checks the
            target, finds nothing, and prepares to insert. Session B runs the same MERGE targeting
            key 42 at nearly the same moment: it also checks the target, also finds nothing (A
            hasn't committed yet), and also prepares to insert. Both sessions now insert a row with
            key 42. If key 42 is protected by a primary key or unique constraint, one of the two
            inserts fails with a primary-key violation at the moment it commits - not because the
            logic was wrong, but because the existence check and the insert weren't executed as one
            atomic, isolated unit against concurrent access.

            This is exactly the kind of race READ COMMITTED is not designed to prevent: it
            guarantees each individual read sees only committed data, but says nothing about two
            transactions' read-then-write sequences interleaving safely. MERGE's own combination of
            a conditional check followed by a conditional insert is a textbook check-then-act race,
            and MERGE provides no extra protection against it beyond whatever isolation level the
            session is running under.

            A WITH (HOLDLOCK) hint (or running the transaction under SERIALIZABLE, which HOLDLOCK
            approximates on the target's lock scope) closes the race by holding the locks acquired
            while evaluating the target key range for the duration of the transaction rather than
            releasing them as soon as the read completes. With HOLDLOCK on the target, session B's
            attempt to check the same key range that session A already touched has to wait for
            session A's transaction to commit or roll back before it can proceed - so B's existence
            check, once it finally runs, correctly sees A's committed insert (if A committed) and
            takes the WHEN MATCHED branch instead of colliding on WHEN NOT MATCHED.
            """,
        HowToFixIt: """
            Add WITH (HOLDLOCK) to the MERGE target (or wrap the MERGE in a transaction running
            under SERIALIZABLE isolation), so the locks taken while evaluating which branch applies
            are held for the duration of the transaction rather than released immediately. This
            forces concurrent MERGE statements targeting overlapping keys to serialize against each
            other instead of racing to the same WHEN NOT MATCHED branch at once.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A MERGE upsert with no concurrency protection on the target",
                NoncompliantSql: """
                    CREATE TABLE dbo.Inventory (Sku VARCHAR(20) NOT NULL PRIMARY KEY, Quantity INT NOT NULL);

                    MERGE dbo.Inventory AS target
                    USING (SELECT '42' AS Sku, 10 AS Quantity) AS source
                        ON target.Sku = source.Sku
                    WHEN MATCHED THEN
                        UPDATE SET Quantity = target.Quantity + source.Quantity
                    WHEN NOT MATCHED THEN
                        INSERT (Sku, Quantity) VALUES (source.Sku, source.Quantity);
                    """,
                NoncompliantExplanation: "Under READ COMMITTED, two sessions running this same MERGE for Sku '42' at nearly the same time can each see no existing row, both take WHEN NOT MATCHED, and one of the two concurrent inserts fails on the PRIMARY KEY constraint.",
                CompliantSql: """
                    MERGE dbo.Inventory WITH (HOLDLOCK) AS target
                    USING (SELECT '42' AS Sku, 10 AS Quantity) AS source
                        ON target.Sku = source.Sku
                    WHEN MATCHED THEN
                        UPDATE SET Quantity = target.Quantity + source.Quantity
                    WHEN NOT MATCHED THEN
                        INSERT (Sku, Quantity) VALUES (source.Sku, source.Quantity);
                    """,
                CompliantExplanation: "HOLDLOCK holds the locks taken while checking Sku '42' for the transaction's duration, so a concurrent MERGE against the same Sku waits instead of racing to the same WHEN NOT MATCHED branch."),
        ]);
}
