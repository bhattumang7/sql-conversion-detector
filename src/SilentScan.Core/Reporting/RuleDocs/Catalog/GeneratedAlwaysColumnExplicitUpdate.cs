using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class GeneratedAlwaysColumnExplicitUpdate
{
    public static string RuleId => SarifRuleCatalog.GeneratedAlwaysColumnAssignmentRuleId(GeneratedAlwaysColumnAssignmentKind.ExplicitUpdateValue);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A system-versioned temporal table's `GENERATED ALWAYS AS ROW START`/`ROW END` period
            columns can never be the target of an `UPDATE`. Oracle-confirmed directly (Docker SQL
            Server 2022): a `SET` clause naming a period column - in a plain `UPDATE`, or inside a
            MERGE `WHEN MATCHED THEN UPDATE` action - fails unconditionally with Msg 13537, "Cannot
            update GENERATED ALWAYS columns", before any row is touched. Unlike the INSERT-side
            restriction, `DEFAULT` is not an escape here: `SET ValidFrom = DEFAULT` fails identically,
            since the column can't be assigned at all, not merely restricted to one specific value.
            """,
        HowToFixIt: """
            Remove the period column from the SET clause - the engine populates it itself and never
            accepts an explicit assignment, DEFAULT included.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An UPDATE SET clause targeting a temporal period column",
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

                    UPDATE dbo.Widget SET ValidFrom = SYSUTCDATETIME() WHERE Id = 1;
                    """,
                NoncompliantExplanation: "ValidFrom is a GENERATED ALWAYS period column - any SET clause naming it fails with Msg 13537, even SET ValidFrom = DEFAULT.",
                CompliantSql: """
                    UPDATE dbo.Widget SET Code = 'XYZ' WHERE Id = 1;
                    """,
                CompliantExplanation: "The period columns are left out of the SET clause entirely - the engine maintains them itself on every UPDATE."),
        ]);
}
