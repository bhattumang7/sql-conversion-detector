using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class SelectiveXmlIndexValueColumn
{
    public static string RuleId => SarifRuleCatalog.SelectiveXmlIndexValueColumnRuleId(SelectiveXmlIndexValueColumnFindingKind.TooWide);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A selective XML index (`CREATE SELECTIVE XML INDEX`) promotes chosen XML paths into
            regular typed columns of an internal node table - each promoted path is declared with
            its own `SQL_DATA_TYPE`, independent of the table's actual columns. A secondary selective
            XML index (`CREATE XML INDEX ... USING XML INDEX ... FOR (path_name)`) then indexes that
            promoted column directly, and the promoted column becomes an ordinary index key - subject
            to the same 900-byte key-length ceiling as any other index key, and to the same "no large
            objects as a key" restriction.

            The primary `CREATE SELECTIVE XML INDEX` statement itself does not enforce either limit:
            a promoted path can be declared `VARCHAR(MAX)` or `NVARCHAR(2000)` without error, because
            at that point the value is just a promoted column, not yet a key. The limit only bites
            when a secondary selective XML index is built over that specific path - oracle-confirmed
            against a real instance, `VARCHAR(MAX)` fails with Msg 6391 ("is promoted to a type that
            is invalid for use as a key column in a secondary selective XML index") and any string
            type wider than 900 bytes (`NVARCHAR(451)` and up, `VARCHAR(901)` and up) fails with Msg
            6395 ("The maximum key length is 900 bytes"). Both are hard CREATE-time failures, not
            warnings - the secondary index never comes into existence.

            This is purely catalog-derived: the promoted path's declared `SQL_DATA_TYPE` and the
            secondary index's `USING XML INDEX ... FOR (path_name)` reference are both known from the
            DDL alone, with no query or live data involved.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A secondary selective XML index over a MAX-typed promoted path",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Payload XML NOT NULL);

                    CREATE SELECTIVE XML INDEX SXI_Orders ON dbo.Orders(Payload)
                    FOR (CustomerNote = '/Order/Note' AS SQL VARCHAR(MAX));

                    CREATE XML INDEX SXI_Orders_Note ON dbo.Orders(Payload)
                    USING XML INDEX SXI_Orders FOR (CustomerNote);
                    """,
                NoncompliantExplanation: "CustomerNote is promoted as VARCHAR(MAX) - the primary selective XML index deploys fine, but the secondary index over it never comes into existence (Msg 6391): a large-object type can never be a key column.",
                CompliantSql: """
                    CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Payload XML NOT NULL);

                    CREATE SELECTIVE XML INDEX SXI_Orders ON dbo.Orders(Payload)
                    FOR (CustomerNote = '/Order/Note' AS SQL VARCHAR(400));

                    CREATE XML INDEX SXI_Orders_Note ON dbo.Orders(Payload)
                    USING XML INDEX SXI_Orders FOR (CustomerNote);
                    """,
                CompliantExplanation: "With a bounded, sub-900-byte declared width, CustomerNote is eligible to be an index key column, and the secondary selective XML index deploys."),
        ]);
}
