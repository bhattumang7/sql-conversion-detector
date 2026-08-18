using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class ColumnCollationDrift
{
    public static string RuleId => SarifRuleCatalog.ColumnCollationDriftRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Every string-family column (`CHAR`, `VARCHAR`, `NCHAR`, `NVARCHAR`, `TEXT`, `NTEXT`)
            carries its own collation - the rules that decide how its bytes compare and sort - and a
            column doesn't have to explicitly declare one to have a specific collation: if the
            `CREATE TABLE`/`ALTER TABLE` statement omits a `COLLATE` clause, the column silently
            inherits the database's default collation at creation time. That inheritance is a
            one-time snapshot, not a live link - if a column was created under one database
            collation and the database's default was changed afterward, or the column was given an
            explicit `COLLATE` that simply doesn't match the database default, the column's own
            recorded collation in `sys.columns` can differ from what every other unqualified string
            column in the database carries, with nothing in a `SELECT *` or a casual schema browse
              making that difference visible.

            That mismatch is a seed for a whole downstream category of problems, not the problem
            itself: any future comparison between this column and a string literal, a parameter, or
            another column carrying the database's baseline collation is now a comparison between two
            different collations. Depending on the specific collations involved, SQL Server either
            raises a hard compile error ("Cannot resolve the collation conflict...") the first time
            someone writes that comparison, or - when the collations are compatible enough to
            resolve automatically - silently inserts an implicit conversion to reconcile them, which
            for the string-comparison case behaves the same way a Verdict-stream ScanForced finding
            does: whichever side has to convert loses the ability to seek an index on it. Detecting
            the drift here, from the catalog alone, catches the seed before either failure mode has a
            chance to show up in a query.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A column collation left behind after a database default changed",
                NoncompliantSql: """
                    -- Database default collation: SQL_Latin1_General_CP1_CI_AS
                    CREATE TABLE dbo.Customers
                    (
                        CustomerId INT           NOT NULL PRIMARY KEY,
                        Name       VARCHAR(100)  COLLATE Latin1_General_CS_AS NOT NULL
                    );
                    """,
                NoncompliantExplanation: "Name is explicitly pinned to Latin1_General_CS_AS while every unqualified string column elsewhere in the database inherits SQL_Latin1_General_CP1_CI_AS - any comparison between Name and a plain literal or another table's unqualified string column risks a collation-conflict compile error or a forced implicit conversion.",
                CompliantSql: """
                    CREATE TABLE dbo.Customers
                    (
                        CustomerId INT           NOT NULL PRIMARY KEY,
                        Name       VARCHAR(100)  COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL
                    );
                    """,
                CompliantExplanation: "Name's collation now matches the database default explicitly - comparisons against other unqualified string columns or literals need no conversion on either side."),
        ]);
}
