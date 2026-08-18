using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.TriggerCorrectness;

internal static class MultiRowUnsafeKeyedDml
{
    public static string RuleId => SarifRuleCatalog.TriggerCorrectnessMultiRowUnsafeKeyedDmlRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            This is the sharper sibling of the plain single-row-assignment bug: the same unguarded
            SELECT @var = column FROM inserted (or deleted) pattern, but here the resulting
            variable doesn't just get logged or read - it drives a keyed UPDATE or DELETE straight
            afterward in the same trigger body, typically WHERE SomeKey = @var. The assignment bug
            on its own silently drops data that would otherwise have been processed; here it goes
            one step further and actually performs a write using the one arbitrary value the engine
            happened to leave in the variable, while the write for every other row in the batch
            never happens at all.

            The mechanism is identical to the plain case - a scalar assignment from a multi-row
            SELECT executes once per row of inserted/deleted in an unspecified, plan-dependent
            order, leaving the variable holding whichever row was assigned last. What makes this
            variant worse in practice is that there's no missing audit row to eventually notice:
            the keyed UPDATE/DELETE actually runs, actually affects a real row, and returns a
            normal-looking @@ROWCOUNT of 1 (or however many rows share that one key). Everything
            about the execution looks like success. The only sign anything is wrong is that the
            other N-1 rows in the firing batch were supposed to get the same treatment and quietly
            didn't - a class of bug that surfaces as "some rows are stale" reports days or weeks
            later, with no error in between to point back at the trigger.
            """,
        HowToFixIt: """
            Replace the single-row variable-driven UPDATE/DELETE with a set-based statement joined
            directly to inserted/deleted, so the WHERE-clause key comes from a join predicate
            instead of a variable, and every row present in inserted/deleted at trigger time
            participates in the write, not just one.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A keyed UPDATE driven by one arbitrarily-captured row",
                NoncompliantSql: """
                    CREATE TABLE dbo.Products
                    (
                        ProductId    INT           NOT NULL PRIMARY KEY,
                        Stock        INT           NOT NULL,
                        LastTouched  DATETIME2     NULL
                    );

                    CREATE TABLE dbo.StockMoves
                    (
                        MoveId     INT NOT NULL PRIMARY KEY,
                        ProductId  INT NOT NULL,
                        Delta      INT NOT NULL
                    );

                    CREATE TRIGGER dbo.trg_StockMoves_Insert ON dbo.StockMoves
                    AFTER INSERT
                    AS
                    BEGIN
                        DECLARE @ProductId INT;
                        SELECT @ProductId = ProductId FROM inserted;

                        UPDATE dbo.Products
                        SET LastTouched = SYSUTCDATETIME()
                        WHERE ProductId = @ProductId;
                    END;
                    """,
                NoncompliantExplanation: "A multi-row insert into StockMoves for several distinct products leaves @ProductId holding only one arbitrary product's id, so the UPDATE touches that single product's row and returns a normal-looking rowcount - the other products moved in the same batch are never stamped, with no error to reveal it.",
                CompliantSql: """
                    CREATE TRIGGER dbo.trg_StockMoves_Insert ON dbo.StockMoves
                    AFTER INSERT
                    AS
                    BEGIN
                        UPDATE p
                        SET p.LastTouched = SYSUTCDATETIME()
                        FROM dbo.Products AS p
                        JOIN inserted AS i ON i.ProductId = p.ProductId;
                    END;
                    """,
                CompliantExplanation: "The UPDATE is joined directly to inserted, so it touches exactly the set of products present in the firing batch - one row or many - with no variable in between to lose rows."),
        ]);
}
