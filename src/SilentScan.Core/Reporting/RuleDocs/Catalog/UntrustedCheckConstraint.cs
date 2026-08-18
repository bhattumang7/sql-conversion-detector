using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class UntrustedCheckConstraint
{
    public static string RuleId => SarifRuleCatalog.UntrustedCheckConstraintRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SQL Server tracks, per CHECK constraint, whether it currently trusts the predicate to
            hold over every existing row (sys.check_constraints.is_not_trusted = 0) or not (= 1).
            The overwhelmingly common way a CHECK ends up untrusted is an ALTER TABLE ... WITH
            NOCHECK ADD CONSTRAINT, or an existing constraint disabled and re-enabled with NOCHECK -
            most often done to add a new business rule to a table that already has rows violating
            it, without having to clean up or reject that data immediately. The constraint still
            enforces the rule going forward for every new INSERT/UPDATE, so the table looks fully
            protected in day-to-day use; only sys.check_constraints reveals that some existing rows
            were never actually verified against it.

            The optimizer relies on a trusted CHECK constraint for more than documentation - it's a
            proven fact about every row in the table that query rewrites can build on. A trusted
            CHECK (Status IN ('Open','Closed')) lets the optimizer eliminate a branch of a UNION ALL
            or a partition that can't contain a given Status value, or skip evaluating a redundant
            predicate the constraint already guarantees. An untrusted constraint forfeits every one
            of those rewrites, because the engine can no longer assume the predicate is actually
            true for rows already sitting in the table - it might be true for every row added since
            the NOCHECK, and false for some row added before it, and the engine has no way to tell
            which without actually checking.
            """,
        HowToFixIt: """
            Run ALTER TABLE ... WITH CHECK CHECK CONSTRAINT against the specific constraint, which
            makes SQL Server scan the existing rows and verify every one satisfies the predicate.
            If they all do, the constraint flips back to trusted and the optimizer can rely on it
            again. If a row genuinely violates it, the ALTER fails and names the constraint, which
            is real signal that the data needs to be fixed or the constraint's predicate needs to be
            reconsidered - not silently left in an unverified state indefinitely.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "WITH NOCHECK leaves the CHECK constraint untrusted",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        OrderId INT          NOT NULL PRIMARY KEY,
                        Status  VARCHAR(10)  NOT NULL
                    );

                    ALTER TABLE dbo.Orders WITH NOCHECK
                        ADD CONSTRAINT CK_Orders_Status
                        CHECK (Status IN ('Open', 'Closed'));
                    """,
                NoncompliantExplanation: "WITH NOCHECK adds the constraint without validating existing Orders rows against it, so sys.check_constraints.is_not_trusted is set to 1 and the optimizer can no longer assume every Status value satisfies the predicate.",
                CompliantSql: """
                    ALTER TABLE dbo.Orders WITH CHECK CHECK CONSTRAINT CK_Orders_Status;
                    """,
                CompliantExplanation: "Forces SQL Server to scan and verify every existing Orders row against the predicate; once it passes, is_not_trusted flips back to 0 and constraint-based rewrites are available again."),
        ]);
}
