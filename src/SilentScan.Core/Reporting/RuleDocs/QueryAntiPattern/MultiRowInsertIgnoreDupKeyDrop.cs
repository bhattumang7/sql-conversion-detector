using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class MultiRowInsertIgnoreDupKeyDrop
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.MultiRowInsertIgnoreDupKeyDrop);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            IGNORE_DUP_KEY is an index option (CREATE UNIQUE INDEX ... WITH (IGNORE_DUP_KEY = ON),
            or a later ALTER INDEX ... REBUILD WITH the same option) that changes what happens when
            a write would violate the index's own uniqueness - but only for INSERT. A single-row
            INSERT that would duplicate an existing key still fails, so IGNORE_DUP_KEY looks like it
            has no effect when tested with one row at a time.

            The real effect only shows up on a multi-row INSERT ... VALUES statement: if any one of
            the rows in the batch collides with an existing key value, or with an earlier row in the
            same batch, that single row is silently skipped - no error, no exception, nothing a
            TRY/CATCH block would ever see. SQL Server reports only an informational message
            ("Duplicate key was ignored") and continues committing every other row in the batch.
            @@ROWCOUNT after the statement reflects only the rows that were actually inserted, which
            is smaller than the number of row constructors the statement text names - but nothing
            forces a caller to check that.

            This is a genuinely useful mechanism for a bulk-load script that expects some rows to
            already exist and wants the rest to succeed regardless. It's also a silent-data-loss trap
            for any other INSERT that assumes every row it names actually lands: application code
            inserting a batch of records has no signal that one was dropped unless it explicitly
            compares @@ROWCOUNT to the number of rows it sent.

            An UPDATE that would create the identical duplicate key is not affected by this option at
            all - it still raises a real, uncaught-by-default error ("Cannot insert duplicate key
            row..."). The silent-skip behavior is specific to INSERT.
            """,
        HowToFixIt: """
            If IGNORE_DUP_KEY's silent-skip behavior is genuinely intended (a bulk load that's
            supposed to tolerate re-inserting rows that already exist), check @@ROWCOUNT against the
            number of rows the statement named and handle the difference explicitly, rather than
            assuming every row landed. If the silent skip isn't intended, remove IGNORE_DUP_KEY from
            the index so a duplicate key raises a real, catchable error the same way a single-row
            INSERT already does.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A multi-row INSERT into a table with an IGNORE_DUP_KEY unique index",
                NoncompliantSql: """
                    CREATE TABLE dbo.Coupons (Code VARCHAR(20) NOT NULL, DiscountPercent INT NOT NULL);
                    CREATE UNIQUE NONCLUSTERED INDEX UX_Coupons_Code ON dbo.Coupons(Code) WITH (IGNORE_DUP_KEY = ON);

                    INSERT INTO dbo.Coupons (Code, DiscountPercent)
                    VALUES ('SAVE10', 10), ('SAVE20', 20), ('SAVE10', 15);
                    -- 'SAVE10' appears twice: the second occurrence is silently dropped, no error,
                    -- and the statement reports success with only 2 of the 3 named rows inserted.
                    """,
                NoncompliantExplanation: "The duplicate 'SAVE10' row is silently skipped (\"Duplicate key was ignored\") - the statement succeeds, and nothing signals that only 2 of the 3 rows were actually written unless the caller checks @@ROWCOUNT.",
                CompliantSql: """
                    INSERT INTO dbo.Coupons (Code, DiscountPercent)
                    VALUES ('SAVE10', 10), ('SAVE20', 20), ('SAVE10', 15);
                    IF @@ROWCOUNT <> 3
                    BEGIN
                        THROW 51000, 'One or more coupon codes were duplicates and were not inserted.', 1;
                    END
                    """,
                CompliantExplanation: "Comparing @@ROWCOUNT to the number of rows the statement named turns the silent skip into a real, catchable signal instead of an invisible partial write."),
        ]);
}
