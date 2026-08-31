using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class ColumnstoreUnsupportedColumnType
{
    public static string RuleId => SarifRuleCatalog.ColumnstoreUnsupportedColumnTypeRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SQL Server has a documented list of data types that cannot participate in a
            columnstore index at all: `SQL_VARIANT`, `XML`, `HIERARCHYID`, `GEOMETRY`,
            `GEOGRAPHY`, `NTEXT`, `TEXT`, `IMAGE`, and `TIMESTAMP`/`ROWVERSION`. This isn't a
            soft restriction the optimizer works around quietly - it's a hard DDL failure.
            Oracle-confirmed directly (real execution, not a documentation claim taken on
            faith): `CREATE CLUSTERED COLUMNSTORE INDEX` takes no column list at all - it
            implicitly covers every column on the table - so the statement fails the instant
            any one of those columns has one of these types, with `Msg 35343: The statement
            failed. Column '...' has a data type that cannot participate in a columnstore
            index.` The identical failure reproduces for `ALTER TABLE ... ADD` such a column
            onto a table that already carries a clustered columnstore index - order doesn't
            matter, only the combination does.

            `VARCHAR(MAX)`/`NVARCHAR(MAX)`/`VARBINARY(MAX)` are a narrower, clustered-vs-
            nonclustered-dependent case: oracle-confirmed a MAX-typed column deploys cleanly
            as part of a clustered columnstore index, but the identical column named in a
            nonclustered columnstore index's own column list fails with the same Msg 35343.

            A nonclustered columnstore index is also narrower in a second way: `CREATE
            NONCLUSTERED COLUMNSTORE INDEX` always takes an explicit column list, so it only
            fails if that list actually names the unsupported-type column - a nonclustered
            columnstore index that simply leaves the column out of its own list is a real,
            legal shape, and this finding does not fire on it.

            This is exactly the class of bug static analysis exists to catch before it reaches
            a deployment pipeline: the DDL parses fine, reads fine, and looks completely
            ordinary - the failure only appears the moment someone actually tries to run it, by
            which point it's a production deployment blocked on a schema-file combination that
            could have been caught by reading the two statements side by side.
            """,
        HowToFixIt: """
            Remove the unsupported-type column from the columnstore index's scope. For a
            clustered columnstore index (which always covers every column), retype the column
            to a concrete columnstore-eligible type, or move it to a separate table that isn't
            columnstore-indexed and join to it. For a nonclustered columnstore index, the
            column list is explicit - simply drop the column from it; the column still exists
            on the table, it just doesn't participate in this particular index. A
            `VARCHAR(MAX)`/`NVARCHAR(MAX)`/`VARBINARY(MAX)` column has a third option: move it
            to a clustered columnstore index instead, where MAX-typed columns are
            columnstore-eligible.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A SQL_VARIANT audit column blocking a clustered columnstore index from deploying",
                NoncompliantSql: """
                    CREATE TABLE dbo.Sales
                    (
                        SaleId      INT             NOT NULL,
                        Amount      INT             NOT NULL,
                        LegacyTag   SQL_VARIANT     NULL
                    );
                    CREATE CLUSTERED COLUMNSTORE INDEX CCI_Sales ON dbo.Sales;
                    -- Fails to deploy: Msg 35343, "LegacyTag has a data type that cannot
                    -- participate in a columnstore index" - CREATE CLUSTERED COLUMNSTORE INDEX
                    -- takes no column list, so every column on the table is in scope.
                    """,
                NoncompliantExplanation: "LegacyTag's mere presence on dbo.Sales - not its use in any query - is enough: a clustered columnstore index implicitly covers every column on the table, so it fails to deploy the moment any one of them is an unsupported type, oracle-confirmed against a real instance.",
                CompliantSql: """
                    CREATE TABLE dbo.Sales
                    (
                        SaleId  INT NOT NULL,
                        Amount  INT NOT NULL
                    );
                    CREATE CLUSTERED COLUMNSTORE INDEX CCI_Sales ON dbo.Sales;

                    CREATE TABLE dbo.SalesLegacyTag
                    (
                        SaleId    INT             NOT NULL PRIMARY KEY,
                        LegacyTag SQL_VARIANT     NULL
                    );
                    """,
                CompliantExplanation: "With LegacyTag moved to a separate, non-columnstore-indexed table, CCI_Sales deploys cleanly - every column it implicitly covers is columnstore-eligible, and LegacyTag can still be joined in whenever it's actually needed."),
            new RuleDocExample(
                Title: "A VARCHAR(MAX) column named in a nonclustered columnstore index's own column list",
                NoncompliantSql: """
                    CREATE TABLE dbo.Invoices
                    (
                        InvoiceId INT             NOT NULL,
                        Amount    INT             NOT NULL,
                        Notes     VARCHAR(MAX)    NULL
                    );
                    CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_Invoices
                        ON dbo.Invoices (Amount, Notes);
                    -- Fails to deploy: Msg 35343, "Notes has a data type that cannot
                    -- participate in a columnstore index" - MAX-typed columns are rejected by
                    -- a NONCLUSTERED columnstore index specifically.
                    """,
                NoncompliantExplanation: "Notes is a MAX-typed column named directly in the nonclustered columnstore index's own column list - oracle-confirmed this combination fails to deploy, even though the identical column deploys cleanly under a clustered columnstore index.",
                CompliantSql: """
                    CREATE TABLE dbo.Invoices
                    (
                        InvoiceId INT             NOT NULL,
                        Amount    INT             NOT NULL,
                        Notes     VARCHAR(MAX)    NULL
                    );
                    CREATE NONCLUSTERED COLUMNSTORE INDEX NCCI_Invoices
                        ON dbo.Invoices (InvoiceId, Amount);
                    """,
                CompliantExplanation: "With Notes left out of the nonclustered columnstore index's own column list, NCCI_Invoices deploys cleanly - Notes still exists on the table, it just isn't part of this index."),
        ]);
}
