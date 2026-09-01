using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Correctness;

internal static class NotNullPredicateContradiction
{
    public static string RuleId => SarifRuleCatalog.NotNullPredicateContradictionRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A column the catalog declares `NOT NULL` can never hold a NULL value in any row the
            engine allows to exist. A `WHERE`/`ON` predicate that tests `IS NULL` against that same
            column can therefore never be TRUE for any row - the branch's result set is provably
            empty before a single row is read. Oracle-confirmed directly (Docker SQL Server 2025): a
            module body with `CREATE TABLE t (amt INT NOT NULL)` and a predicate
            `WHERE amt IS NULL` compiles to a bare `Constant Scan`, with the table never touched at
            all - the engine itself proves the predicate unsatisfiable at compile time, independent
            of any CHECK constraint.

            The column's own outer-join side is accounted for: on the null-supplying side of an
            OUTER JOIN, an unmatched row's columns read back as NULL regardless of what the base
            table's own `NOT NULL` declares, so this rule never fires there - only a column that is
            genuinely, unconditionally NOT NULL at the point the predicate reads it is treated as a
            trusted fact.
            """,
        HowToFixIt: """
            Remove the IS NULL test, or correct the column reference - a column the catalog declares
            NOT NULL can never satisfy it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "IS NULL against a column the catalog declares NOT NULL",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        OrderId INT NOT NULL PRIMARY KEY,
                        Amount  INT NOT NULL
                    );

                    SELECT OrderId FROM dbo.Orders WHERE Amount IS NULL;
                    """,
                NoncompliantExplanation: "Amount is declared NOT NULL, so no row can ever have Amount IS NULL - the optimizer itself proves this and folds the plan to a Constant Scan.",
                CompliantSql: """
                    SELECT OrderId FROM dbo.Orders WHERE Amount IS NOT NULL;
                    """,
                CompliantExplanation: "IS NOT NULL against a NOT NULL column is always TRUE rather than provably unsatisfiable, and reads as the intended no-op guard rather than a dead branch."),
        ]);
}
