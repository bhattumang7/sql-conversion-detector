using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.WindowFunctionArgument;

internal static class TableSamplePercentOutOfRange
{
    public static string RuleId => SarifRuleCatalog.WindowFunctionArgumentRuleId(WindowFunctionArgumentFindingKind.TableSamplePercentOutOfRange);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            TABLESAMPLE (... PERCENT)'s percent argument names a percentage of the table's rows,
            and only the inclusive range [0, 100] is a valid percentage - 0 samples zero rows, 100
            samples the whole table, and both ends were probed directly against a real engine and
            confirmed valid. A percent argument that constant-folds to a value outside that range -
            whether written as a bare literal or as an arithmetic expression that folds to one -
            raises Msg 476 ("The PERCENT tablesample size must be between 0 and 100") at compile
            time, before any row is ever touched.

            This is pure source-level constant-folding with no catalog dependency - the same shape
            as the LAG/LEAD offset argument and the PERCENTILE_CONT/PERCENTILE_DISC percentile
            argument being compile-time constants regardless of whether they are literals or
            expressions that fold to one.
            """,
        HowToFixIt: """
            Change the TABLESAMPLE percent argument to a value between 0 and 100 inclusive.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A TABLESAMPLE clause with an out-of-range percent",
                NoncompliantSql: "SELECT * FROM dbo.Sales TABLESAMPLE (150 PERCENT);",
                NoncompliantExplanation: "The percent argument 150 constant-folds to a value outside [0, 100] - the statement never compiles (Msg 476).",
                CompliantSql: "SELECT * FROM dbo.Sales TABLESAMPLE (10 PERCENT);",
                CompliantExplanation: "10 is inside the inclusive [0, 100] range and samples roughly a tenth of the table."),
        ]);
}
