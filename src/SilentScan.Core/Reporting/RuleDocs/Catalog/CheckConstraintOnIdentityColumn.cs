using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class CheckConstraintOnIdentityColumn
{
    public static string RuleId => SarifRuleCatalog.CheckConstraintOnIdentityColumnRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An IDENTITY column's counter is a property of the column definition itself, generated
            by the storage engine before the row's own constraints are ever evaluated - and
            critically, the counter advances whether or not the INSERT that consumed it actually
            succeeds. If a row fails a CHECK constraint, a NOT NULL violation, or any other
            constraint, the identity value already reserved for that failed row is gone; it is
            never reused. This is documented, expected IDENTITY behavior (the same reason
            IDENTITY values are known to have gaps after a rolled-back transaction or a failed
            insert), but it interacts badly with a CHECK constraint that references the IDENTITY
            column directly with a numeric threshold.

            CHECK (Id > 1000) on a column defined IDENTITY(1,1) doesn't wait for Id to reach 1001
            before starting to matter - it evaluates on every insert from the very first row. The
            first attempted insert gets Id = 1, fails the CHECK, and is rejected - but the identity
            counter still advances to 2 because the value was already consumed before the
            constraint was evaluated. The next insert gets Id = 2, also fails, also advances the
            counter. This repeats for every insert until the counter finally reaches 1001, at which
            point the CHECK starts passing - not because any application logic changed, but purely
            because 1000 identity values were burned failing inserts one at a time. From that point
            forward the constraint is permanently satisfied by construction (IDENTITY only
            increases), so it silently stops doing anything at all, forever, with no code change
            and no error after the fact - only the trail of 1000 rejected inserts and a counter that
            jumped straight to 1001 hints at what happened.

            The same counter-drift problem applies to every predicate shape referencing the
            IDENTITY column directly, just not always in this direction. A reversed one-sided
            threshold like CHECK (Id < 1000) is the mirror image: it accepts inserts while the
            counter is still low and then permanently, silently starts rejecting every insert once
            the counter passes 1000 - the opposite failure mode from "stops mattering." A predicate
            that isn't a one-sided threshold at all, like CHECK (Id % 2 = 0), doesn't settle in
            either direction - it keeps alternating pass/fail forever as the counter advances,
            burning half of every subsequent identity value on a rejected insert indefinitely.
            """,
        HowToFixIt: """
            Do not enforce a numeric-threshold CHECK constraint directly against an IDENTITY
            column - the counter advances on failed inserts as much as successful ones, so the
            constraint's effect drifts as the counter climbs instead of staying fixed: a one-sided
            "greater than" threshold rejects a burst of inserts while the counter climbs toward it
            and then becomes permanently, silently satisfied; a one-sided "less than" threshold
            does the reverse, accepting inserts only until the counter passes it and then
            permanently rejecting everything after; and any other predicate shape (equality,
            modulo, a range) isn't guaranteed to ever settle into either state. If the actual
            intent is a business rule about the row's identity/sequence, express it against a value
            the application controls and can validate before submission, not the engine-generated
            IDENTITY value itself.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A threshold CHECK against the IDENTITY column itself",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        OrderId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                        CONSTRAINT CK_Orders_OrderId CHECK (OrderId > 1000)
                    );

                    INSERT INTO dbo.Orders DEFAULT VALUES;
                    """,
                NoncompliantExplanation: "The first insert receives OrderId = 1 from the identity counter, fails CHECK (OrderId > 1000), and is rejected - but the counter has already advanced to 2. The next 999 inserts repeat this, burning identity values 1 through 1000 on rejected rows, until OrderId finally reaches 1001 and the constraint starts silently passing forever."),
        ]);
}
