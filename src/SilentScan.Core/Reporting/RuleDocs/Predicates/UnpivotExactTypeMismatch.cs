using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class UnpivotExactTypeMismatch
{
    public static string RuleId => SarifRuleCatalog.UnpivotExactTypeMismatchRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            UNPIVOT requires every column named in its IN-list to share exactly the same type -
            oracle-confirmed (Msg 8167, "The type of column '...' conflicts with the type of
            other columns specified in the UNPIVOT list") this is stricter than ordinary implicit
            convertibility: INT vs BIGINT conflicts, VARCHAR(10) vs VARCHAR(20) conflicts, and
            even two VARCHAR(10) columns under different collations conflict, even though all of
            those pairs would otherwise convert freely in a comparison or assignment. It's
            decidable directly from the source table's own catalog column types.
            """,
        HowToFixIt: """
            Change every column named in the UNPIVOT IN-list to share one identical type - same
            base type, same length/precision/scale, and same collation - or CAST the source
            columns to a common type before unpivoting.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "UNPIVOT over columns with different declared lengths",
                NoncompliantSql: """
                    CREATE TABLE dbo.Metric (Id INT NOT NULL PRIMARY KEY, A VARCHAR(10), B VARCHAR(20));
                    SELECT * FROM dbo.Metric UNPIVOT (Val FOR ColName IN (A, B)) AS u;
                    """,
                NoncompliantExplanation: "dbo.Metric.A is VARCHAR(10) and dbo.Metric.B is VARCHAR(20) - different lengths conflict with Msg 8167.",
                CompliantSql: """
                    CREATE TABLE dbo.Metric (Id INT NOT NULL PRIMARY KEY, A VARCHAR(20), B VARCHAR(20));
                    SELECT * FROM dbo.Metric UNPIVOT (Val FOR ColName IN (A, B)) AS u;
                    """,
                CompliantExplanation: "Both columns are VARCHAR(20), so the types match exactly and UNPIVOT compiles."),
        ]);
}
