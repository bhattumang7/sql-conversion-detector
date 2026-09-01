using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class AlterTableSwitchColumnMismatch
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.AlterTableSwitchColumnMismatch);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            ALTER TABLE ... SWITCH is a metadata-only operation - it reassigns a partition's (or a
            whole table's) storage between two tables without moving any data, which is exactly
            why it's used for near-instant bulk load/archive against a partitioned table. But that
            speed only works because the engine requires the source and target tables to already
            have identical physical shapes: same column count, same column names in the same
            order, same data types (including collation on character columns), same
            computed-column status. If they don't match, the engine has no way to reinterpret the
            switched-in rows correctly, so it refuses the whole statement outright rather than
            attempt it.

            This is a genuine hard failure, not a silent-degradation risk - the statement raises a
            real, specific error (4943 for a column-count mismatch, 4942 for a renamed column,
            4965 for a computed-column mismatch, 4944 for a type/length/precision/scale mismatch,
            4945 for a collation mismatch on a character column) and nothing switches. The trap is
            upstream of that: a staging table built by one script
            and a partitioned production table maintained by another naturally drift apart over
            time - an added column, a widened VARCHAR, a NULL constraint changed on one side but
            not the other - and the SWITCH that used to work silently stops working the moment
            that drift happens, usually discovered only when a scheduled load job fails.

            SQL Server checks column shape purely from catalog metadata, before ever looking at
            the data in either table - so this mismatch is fully decidable by reading both tables'
            definitions, with no need to run the statement to know it will fail.
            """,
        HowToFixIt: """
            Make the source and target tables' column definitions match exactly at every ordinal
            position: same names, same order, same data types (including
            length/precision/scale/collation), and the same computed-column status. If the two
            tables are meant to stay switchable
            long-term, keep whichever DDL creates/alters either table in sync with the other -
            for example, generate the staging table's DDL from the production table's own
            definition rather than maintaining two copies by hand.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A staging table whose column type has drifted from the partitioned target",
                NoncompliantSql: """
                    CREATE TABLE dbo.OrdersStaging (Id INT NOT NULL, Amount DECIMAL(10,2) NOT NULL);
                    CREATE TABLE dbo.Orders (Id INT NOT NULL, Amount DECIMAL(12,4) NOT NULL);
                    -- (Orders is partitioned; OrdersStaging holds a batch to load in.)

                    ALTER TABLE dbo.OrdersStaging SWITCH TO dbo.Orders PARTITION 1;
                    -- Msg 4944: column 'Amount' has data type decimal(10,2) in source table
                    -- 'OrdersStaging' which is different from its type decimal(12,4) in target
                    -- table 'Orders'.
                    """,
                NoncompliantExplanation: "The two tables' Amount columns no longer share the same precision/scale - the engine refuses the SWITCH outright with error 4944, before touching any row.",
                CompliantSql: """
                    CREATE TABLE dbo.OrdersStaging (Id INT NOT NULL, Amount DECIMAL(12,4) NOT NULL);
                    CREATE TABLE dbo.Orders (Id INT NOT NULL, Amount DECIMAL(12,4) NOT NULL);

                    ALTER TABLE dbo.OrdersStaging SWITCH TO dbo.Orders PARTITION 1;
                    """,
                CompliantExplanation: "Both tables declare Amount as the identical decimal(12,4) - the column-shape check passes and the SWITCH proceeds to the engine's remaining (data-dependent) checks."),
        ]);
}
