using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class UnindexedForeignKey
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.UnindexedForeignKey);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A real FOREIGN KEY constraint's own full parent-side column set - the columns it actually
            references on the parent table - having no active, non-filtered, non-columnstore index
            leading on it means two real costs at once: every parent-side DELETE or UPDATE the engine
            has to referential-integrity-check against the child table forces a full scan of the
            child table to look for referencing rows, and every join application code writes along
            this relationship has no seek path either.

            The comparison is composite-aware and order-tolerant on the FK side - the index's own
            first N key columns (N being the FK's own column count) have to form exactly the FK's
            column set, the same shape this codebase's own predicate-layer scanners already use for
            related uniqueness/join checks elsewhere. A filtered index covering the right columns
            still counts as "no index" for this rule's purposes, since a filtered index's own WHERE
            predicate might not cover every row the referential-integrity check needs to see.
            """,
        HowToFixIt: """
            Add an index leading on the foreign key's parent-side column set.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A foreign key column with no supporting index",
                NoncompliantSql: """
                    CREATE TABLE dbo.Customers (Id INT NOT NULL PRIMARY KEY);
                    CREATE TABLE dbo.Orders
                    (
                        Id         INT NOT NULL PRIMARY KEY,
                        CustomerId INT NOT NULL,
                        CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId) REFERENCES dbo.Customers(Id)
                    );
                    -- No index on dbo.Orders(CustomerId).
                    """,
                NoncompliantExplanation: "Deleting a row from dbo.Customers forces the engine to scan the entire dbo.Orders table to check for referencing rows, since CustomerId has no supporting index - and every join from Orders to Customers has no seek path either.",
                CompliantSql: """
                    CREATE INDEX IX_Orders_CustomerId ON dbo.Orders(CustomerId);
                    """,
                CompliantExplanation: "The index gives both the referential-integrity check and any join along this relationship a real seek path instead of a full scan."),
        ]);
}
