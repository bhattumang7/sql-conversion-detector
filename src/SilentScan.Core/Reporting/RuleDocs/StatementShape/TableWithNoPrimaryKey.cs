using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.StatementShape;

internal static class TableWithNoPrimaryKey
{
    public static string RuleId => SarifRuleCatalog.StatementShapeRuleId(StatementShapeFindingKind.TableWithNoPrimaryKey);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A base table with no `PRIMARY KEY` constraint at all has no engine-enforced row
            uniqueness - nothing stops two rows from being byte-for-byte identical, and nothing
            gives any other part of the system a reliable way to name "this one specific row"
            distinct from every other. This is a common, structural root cause of an accidental
            duplicate-row bug that nothing catches at the schema level: an application-layer retry,
            a re-run import, or a race between two concurrent inserts can each silently produce a
            duplicate the database itself has no mechanism to reject.

            It also blocks real engine features that specifically require a primary key to function
            at all - transactional replication and change tracking both need a reliable way to
            identify a row uniquely, and a table with no primary key simply can't participate in
            either until one is added.

            This is a purely catalog-level structural fact - one fact per table, no AST needed, the
            same "one structural fact per table" shape this codebase's MAX-typed-column finding
            uses.
            """,
        HowToFixIt: """
            Add a PRIMARY KEY constraint to the table.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A table with no PRIMARY KEY constraint",
                NoncompliantSql: """
                    CREATE TABLE dbo.ImportedOrders
                    (
                        OrderId    INT NOT NULL,
                        CustomerId INT NOT NULL,
                        Amount     DECIMAL(10,2) NOT NULL
                    );
                    """,
                NoncompliantExplanation: "Nothing prevents two byte-for-byte identical rows from existing in this table, and it can't participate in transactional replication or change tracking, both of which require a primary key.",
                CompliantSql: """
                    CREATE TABLE dbo.ImportedOrders
                    (
                        OrderId    INT NOT NULL PRIMARY KEY,
                        CustomerId INT NOT NULL,
                        Amount     DECIMAL(10,2) NOT NULL
                    );
                    """,
                CompliantExplanation: "The PRIMARY KEY gives the engine a real, enforced way to identify each row uniquely and reject duplicates."),
        ]);
}
