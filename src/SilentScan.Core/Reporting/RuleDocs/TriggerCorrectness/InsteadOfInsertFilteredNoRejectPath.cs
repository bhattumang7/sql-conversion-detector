using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.TriggerCorrectness;

internal static class InsteadOfInsertFilteredNoRejectPath
{
    public static string RuleId => SarifRuleCatalog.TriggerCorrectnessInsteadOfInsertFilteredNoRejectPathRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An INSTEAD OF INSERT trigger completely replaces the caller's INSERT: SQL Server never
            performs the original write itself, and whatever the trigger body does - or doesn't do
            - is the entire effect of the statement. This is what makes INSTEAD OF triggers useful
            for enforcing rules on updatable views or for redirecting writes, but it also means the
            trigger carries the full responsibility for every row the caller submitted. If the
            trigger body re-inserts only a WHERE- or JOIN-filtered subset of inserted - for example
            only rows that pass a business validation join - the rows that don't pass the filter
            are simply never written anywhere.

            Critically, none of this is visible to the caller. The original INSERT statement
            completes successfully; SQL Server does not compare how many rows the caller submitted
            against how many rows the trigger actually wrote, and raises no error, warning, or
            truncation-style signal for the gap. @@ROWCOUNT after the statement reflects whatever
            the trigger's own last statement affected, not the original inserted row count, so even
            @@ROWCOUNT doesn't naturally reveal the discrepancy unless the caller specifically
            compares it against the batch size they submitted - something essentially no caller
            does for an INSERT. From the application's point of view, an INSERT of 100 rows that
            silently becomes 80 real inserts looks identical to a clean INSERT of 100.

            This is the same silent-data-loss shape as a WHERE clause that matches fewer rows than
            expected, except worse: an ordinary UPDATE/DELETE at least reports its own row count
            faithfully, so a caller checking rowcount against expectations has a chance of catching
            it. An INSTEAD OF INSERT trigger interposes an entirely separate statement between the
            caller's intent and the actual write, and nothing in the caller-visible contract of an
            INSERT statement carries any signal that some of the submitted rows never landed.
            """,
        HowToFixIt: """
            Add a companion branch that explicitly handles the rows the WHERE/JOIN filter excludes,
            instead of letting them fall out silently. Depending on what "handling" means for the
            case at hand, this might mean writing the excluded rows to a rejection/staging table,
            raising an error that fails the whole statement when any row is excluded (via
            RAISERROR/THROW after checking for excluded rows), or explicitly reporting the excluded
            row count back to the caller through an output mechanism the caller actually checks.
            Whichever shape is right, the excluded rows must be accounted for somewhere the caller
            can observe, not just left out of the re-INSERT.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An INSTEAD OF INSERT trigger drops rows that fail a validation join",
                NoncompliantSql: """
                    CREATE TABLE dbo.Products
                    (
                        ProductId   INT           NOT NULL PRIMARY KEY,
                        CategoryId  INT           NOT NULL,
                        Name        VARCHAR(100)  NOT NULL
                    );

                    CREATE TABLE dbo.Categories
                    (
                        CategoryId INT NOT NULL PRIMARY KEY
                    );

                    CREATE VIEW dbo.ProductEntry AS
                    SELECT ProductId, CategoryId, Name FROM dbo.Products;
                    GO

                    CREATE TRIGGER dbo.trg_ProductEntry_Insert ON dbo.ProductEntry
                    INSTEAD OF INSERT
                    AS
                    BEGIN
                        INSERT INTO dbo.Products (ProductId, CategoryId, Name)
                        SELECT i.ProductId, i.CategoryId, i.Name
                        FROM inserted AS i
                        JOIN dbo.Categories AS c ON c.CategoryId = i.CategoryId;
                    END;
                    """,
                NoncompliantExplanation: "A caller INSERTing 50 rows into dbo.ProductEntry, three of which reference a CategoryId that doesn't exist in dbo.Categories, sees the INSERT complete successfully - only 47 rows are actually written to dbo.Products, and the caller has no way to learn that three rows were dropped.",
                CompliantSql: """
                    CREATE TRIGGER dbo.trg_ProductEntry_Insert ON dbo.ProductEntry
                    INSTEAD OF INSERT
                    AS
                    BEGIN
                        IF EXISTS (
                            SELECT 1 FROM inserted AS i
                            WHERE NOT EXISTS (SELECT 1 FROM dbo.Categories AS c WHERE c.CategoryId = i.CategoryId)
                        )
                        BEGIN
                            THROW 50001, 'One or more rows reference a CategoryId that does not exist.', 1;
                        END;

                        INSERT INTO dbo.Products (ProductId, CategoryId, Name)
                        SELECT ProductId, CategoryId, Name
                        FROM inserted;
                    END;
                    """,
                CompliantExplanation: "The trigger now fails the whole statement with an explicit error the caller cannot ignore when any row would have been excluded, instead of silently re-inserting only the rows that pass."),
        ]);
}
