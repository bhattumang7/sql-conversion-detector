using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class GeneratedAlwaysColumnExplicitInsert
{
    public static string RuleId => SarifRuleCatalog.GeneratedAlwaysColumnAssignmentRuleId(GeneratedAlwaysColumnAssignmentKind.ExplicitInsertValue);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A system-versioned temporal table's `GENERATED ALWAYS AS ROW START`/`ROW END` period
            columns are populated by the engine itself on every INSERT - it never accepts a caller-
            supplied value for one. Oracle-confirmed directly (Docker SQL Server 2022): an `INSERT`
            (or a MERGE `WHEN NOT MATCHED THEN INSERT`) whose column list names a period column and
            supplies it a non-`DEFAULT` value fails with Msg 13536, "Cannot insert an explicit value
            into a GENERATED ALWAYS column", before a single row is written - not a data-dependent
            failure, and not specific to the `VALUES` clause: naming the column at all from a
            `SELECT`/`EXEC` row source fails the same way, since neither can supply the `DEFAULT`
            keyword.

            `DEFAULT` is the one value the engine accepts in a period column's position - explicitly
            in a `VALUES` row, or implicitly by leaving the column out of the column list entirely
            (including the fully-implicit `INSERT INTO t VALUES (...)` form, provided the value
            supplied at the period column's own ordinal position is `DEFAULT`) - so this rule never
            fires on either of those shapes.
            """,
        HowToFixIt: """
            Drop the period column from the INSERT's column list and let the engine populate it, or
            supply DEFAULT for it explicitly in every VALUES row.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An explicit value for a temporal period column",
                NoncompliantSql: """
                    CREATE TABLE dbo.Widget
                    (
                        Id   INT NOT NULL PRIMARY KEY,
                        Code VARCHAR(20) NOT NULL,
                        ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START,
                        ValidTo   DATETIME2 GENERATED ALWAYS AS ROW END,
                        PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
                    )
                    WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.WidgetHistory));

                    INSERT INTO dbo.Widget (Id, Code, ValidFrom) VALUES (1, 'ABC', SYSUTCDATETIME());
                    """,
                NoncompliantExplanation: "ValidFrom is a GENERATED ALWAYS period column - naming it in the column list with a real value fails with Msg 13536 before any row is written.",
                CompliantSql: """
                    INSERT INTO dbo.Widget (Id, Code) VALUES (1, 'ABC');
                    """,
                CompliantExplanation: "Leaving ValidFrom/ValidTo out of the column list lets the engine populate both period columns itself."),
        ]);
}
