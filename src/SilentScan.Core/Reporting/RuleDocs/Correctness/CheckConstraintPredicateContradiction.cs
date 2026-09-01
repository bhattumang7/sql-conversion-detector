using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Correctness;

internal static class CheckConstraintPredicateContradiction
{
    public static string RuleId => SarifRuleCatalog.CheckConstraintPredicateContradictionIntervalRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A trusted, enabled `CHECK` constraint is a standing guarantee: every row the engine
            allows into the table already satisfies it, permanently, without any query having to
            re-check it. When a `WHERE` predicate compares that same column against a literal (or a
            literal range) that lies entirely outside the constraint's own interval, no row in the
            table - now or ever, while the constraint stays trusted - can satisfy that predicate.
            The result set for that branch is provably empty before a single row is read.

            This isn't a guess about typical data - it's a fact SQL Server's own optimizer proves at
            compile time. Oracle-confirmed directly (Docker SQL Server 2025): a module body with
            `CREATE TABLE t (amt INT CHECK (amt > 0))` and a predicate `WHERE amt < 0` compiles to a
            bare `Constant Scan`, with the table never touched at all - the same plan shape the
            engine produces for a literal `WHERE 1 = 0`. The same fold happens for an `AND`-combined
            range CHECK, an `OR`-combined CHECK, and a `BETWEEN`-shaped query predicate. It does NOT
            happen when the CHECK constraint is `NOT TRUSTED` (added `WITH NOCHECK` and never
            revalidated) - oracle-confirmed the optimizer leaves the plan as an ordinary scan in that
            case, correctly refusing to rely on a constraint it can't vouch for existing data
            against.

            Scope: only single-column CHECK constraints built purely from `AND`/`OR`/`BETWEEN` over
            numeric-literal comparisons are used as a source of trusted facts. A CHECK constraint
            that spans more than one column, compares against a string literal, calls a function, or
            uses an `IN` list is never folded into an interval and never contributes a finding - a
            deliberate scope narrowing to keep every reported contradiction to the exact shape the
            optimizer itself is confirmed to fold.
            """,
        HowToFixIt: """
            Remove or correct the literal so it falls inside the CHECK constraint's own interval - it
            can never match a row the constraint allows to exist. If the constraint itself no longer
            reflects the intended data range, alter the constraint instead of leaving a predicate
            that silently returns nothing.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A literal predicate falls entirely outside a trusted CHECK constraint's interval",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        OrderId INT NOT NULL PRIMARY KEY,
                        Amount  INT NOT NULL CHECK (Amount > 0)
                    );

                    SELECT OrderId FROM dbo.Orders WHERE Amount < 0;
                    """,
                NoncompliantExplanation: "Every row the trusted CHECK constraint allows into Orders already has Amount > 0, so Amount < 0 can never be TRUE for any row - the optimizer itself proves this and folds the plan to a Constant Scan.",
                CompliantSql: """
                    SELECT OrderId FROM dbo.Orders WHERE Amount > 100;
                    """,
                CompliantExplanation: "100 lies inside the CHECK constraint's own interval (Amount > 0), so this predicate can genuinely match rows and the optimizer scans the table normally."),
        ]);
}
