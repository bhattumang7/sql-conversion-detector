using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class CascadingForeignKey
{
    public static string RuleId => SarifRuleCatalog.CascadingForeignKeyRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A foreign key whose `ON DELETE`/`ON UPDATE` action is anything other than the default
            `NO ACTION` - `CASCADE`, `SET NULL`, `SET DEFAULT` - makes a single DML statement
            against the referenced (parent) table silently touch every dependent row in the child
            table too, with no visible predicate change at the call site. A plain `DELETE FROM
            dbo.Orders WHERE Id = 1` reads as a single-row delete from its own text, but if
            `dbo.OrderLines` has a cascading foreign key back to `Orders`, that one statement also
            deletes (or nulls, or resets) every matching row in `OrderLines` - real, hidden
            multi-table work the statement's own text gives no hint of.

            This is purely informational and unconditional - reported once per cascading foreign
            key, independent of whether any scanned DML actually deletes or updates the parent
            table, since it's a stable schema fact rather than something tied to a particular
            query. It makes no claim about magnitude (how many child rows, how often the cascade
            actually fires) - only that the cascade exists and is worth being aware of before
            writing or reviewing DML against the parent table.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A foreign key with ON DELETE CASCADE",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders (Id INT NOT NULL PRIMARY KEY);
                    CREATE TABLE dbo.OrderLines
                    (
                        Id      INT NOT NULL PRIMARY KEY,
                        OrderId INT NOT NULL,
                        CONSTRAINT FK_OrderLines_Orders FOREIGN KEY (OrderId)
                            REFERENCES dbo.Orders (Id) ON DELETE CASCADE
                    );

                    DELETE FROM dbo.Orders WHERE Id = 1;
                    """,
                NoncompliantExplanation: "This DELETE's own text names only dbo.Orders, but ON DELETE CASCADE means it also silently deletes every dbo.OrderLines row referencing order 1 - real, hidden multi-table work with no predicate visible at the call site."),
        ]);
}
