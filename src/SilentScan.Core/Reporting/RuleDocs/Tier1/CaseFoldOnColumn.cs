using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Tier1;

internal static class CaseFoldOnColumn
{
    public static string RuleId => SarifRuleCatalog.Tier1RuleId(SargabilityFindingKind.CaseFoldOnColumn);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            UPPER(Name) = 'SMITH' or LOWER(Email) = @email is a habit carried over from languages
            and databases where string comparison is case-sensitive by default and case-folding is
            how you make a comparison case-insensitive. SQL Server usually isn't one of those
            databases: the default collation for most SQL Server installations and columns
            (anything ending in _CI, "case-insensitive") already treats 'Smith' and 'SMITH' as
            equal for comparison purposes, with no function call needed. Wrapping the column in
            UPPER/LOWER anyway doesn't change which rows match under a case-insensitive collation -
            it changes nothing about the result - but it does the same thing every other
            function-wrapped-column pattern does: it forces the engine to evaluate the function on
            every row before comparing, defeating any index seek on the raw column.

            The fix genuinely depends on the column's real collation, which is why this rule's
            remediation branches on it rather than giving one blanket answer. Under a
            case-insensitive collation, the UPPER/LOWER call is pure overhead with no semantic
            effect - delete it. Under a case-sensitive collation (_CS, deliberately chosen for a
            column that needs to distinguish "Smith" from "SMITH" as different values), the
            UPPER/LOWER call is doing real work by changing the comparison's semantics, and simply
            deleting it would change which rows match - a genuinely different rewrite is needed
            there.
            """,
        HowToFixIt: """
            First determine the column's actual collation (sys.columns.collation_name, or
            sp_help on the table). If it's case-insensitive (the common case), delete the
            UPPER/LOWER wrap entirely - the comparison already ignores case, and the column is now
            bare for the optimizer to seek on. If the collation is genuinely case-sensitive and the
            case-insensitive comparison is intentional, either apply a case-insensitive COLLATE
            clause to the LITERAL/parameter side of the comparison instead of the column
            (col = 'Smith' COLLATE SQL_Latin1_General_CP1_CI_AS), or maintain a separate indexed
            computed column holding the case-folded value and compare against that instead.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "UPPER() on a case-insensitive column is pure overhead",
                NoncompliantSql: """
                    CREATE TABLE dbo.Customers
                    (
                        CustomerId INT           NOT NULL PRIMARY KEY,
                        Name       NVARCHAR(100) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL
                    );
                    CREATE INDEX IX_Customers_Name ON dbo.Customers(Name);

                    SELECT CustomerId
                    FROM dbo.Customers
                    WHERE UPPER(Name) = 'SMITH';
                    """,
                NoncompliantExplanation: "The column's own collation (CI = case-insensitive) already matches 'Smith' against 'SMITH' with no function call - UPPER(Name) changes nothing about the result but forces a per-row evaluation that defeats IX_Customers_Name.",
                CompliantSql: """
                    SELECT CustomerId
                    FROM dbo.Customers
                    WHERE Name = 'SMITH';
                    """,
                CompliantExplanation: "Identical result under the column's own case-insensitive collation, with Name now bare for the index to seek on."),
        ]);
}
