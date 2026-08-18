using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class UntrustedForeignKey
{
    public static string RuleId => SarifRuleCatalog.UntrustedForeignKeyRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A foreign key isn't just documentation of intent - the optimizer actually relies on it
            being true. SQL Server tracks, per foreign key, whether it currently trusts the
            constraint to hold over every existing row (sys.foreign_keys.is_not_trusted = 0) or
            not (= 1). A trusted FK lets the optimizer perform join elimination: if a query joins
            Orders to Customers only to pull columns that already live on Orders (or only to
            filter on the join's existence, never Customers' own columns), and a trusted FK
            guarantees every Orders.CustomerId has a matching Customers.CustomerId, the optimizer
            can prove the join can't add or remove rows or change any value the query actually
            needs - so it skips touching the Customers table at all. An untrusted FK forfeits that
            rewrite outright, because the engine can no longer prove the referential relationship
            actually holds; it must perform the join for real, every time, on every query that
            would otherwise have benefited.

            The overwhelmingly common way a FK ends up untrusted is an ALTER TABLE ... WITH NOCHECK
            ADD CONSTRAINT, or an existing constraint disabled and re-enabled with NOCHECK - most
            often done to load a batch of data that doesn't yet satisfy the constraint, or to avoid
            paying the cost of validating years of existing rows. That NOCHECK does exactly what it
            says: it adds the constraint without verifying old rows against it, and SQL Server
            marks the constraint untrusted because it genuinely doesn't know whether every row
            complies. The constraint still enforces the rule going forward, so it looks fully
            active in the object browser and in day-to-day behavior - only sys.foreign_keys reveals
            that the engine isn't relying on it for its own optimization decisions.
            """,
        HowToFixIt: """
            Run ALTER TABLE ... WITH CHECK CHECK CONSTRAINT against the specific constraint, which
            makes SQL Server actually scan the existing rows and verify every one satisfies the
            relationship. If they all do, the constraint flips back to trusted and join elimination
            becomes available again. If a row genuinely violates it, the ALTER fails and identifies
            the bad data, which is real signal to clean up before re-trusting the constraint - the
            NOCHECK originally sidestepped that problem rather than solving it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "WITH NOCHECK leaves the FK untrusted",
                NoncompliantSql: """
                    CREATE TABLE dbo.Customers
                    (
                        CustomerId INT NOT NULL PRIMARY KEY
                    );

                    CREATE TABLE dbo.Orders
                    (
                        OrderId    INT NOT NULL PRIMARY KEY,
                        CustomerId INT NOT NULL
                    );

                    ALTER TABLE dbo.Orders WITH NOCHECK
                        ADD CONSTRAINT FK_Orders_Customers
                        FOREIGN KEY (CustomerId) REFERENCES dbo.Customers (CustomerId);
                    """,
                NoncompliantExplanation: "WITH NOCHECK adds the constraint without validating existing Orders rows against it, so sys.foreign_keys.is_not_trusted is set to 1 and the optimizer can't use this FK for join elimination.",
                CompliantSql: """
                    ALTER TABLE dbo.Orders WITH CHECK CHECK CONSTRAINT FK_Orders_Customers;
                    """,
                CompliantExplanation: "Forces SQL Server to scan and verify every existing Orders row against the constraint; once it passes, is_not_trusted flips back to 0 and join elimination is available again."),
        ]);
}
