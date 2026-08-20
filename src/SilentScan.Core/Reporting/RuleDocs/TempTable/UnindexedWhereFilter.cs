using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.TempTable;

internal static class UnindexedWhereFilter
{
    public static string RuleId => SarifRuleCatalog.UnindexedTempTableUsageRuleId(UnindexedTempTableUsageKind.FilteredInWhere);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The join-operand sibling of this rule covers a `#temp` table used in a JOIN with no
            supporting index; this one covers the same underlying cost for a `#temp` table filtered
            directly by its own WHERE clause instead. A `SELECT ... INTO #t` populates the temp
            table with no indexes of its own, and if nothing creates one before a later `SELECT *
            FROM #t WHERE Code = 'X'`-shaped query runs against it, that WHERE predicate has no seek
            path available at all - the engine must scan every row of #t to evaluate the filter,
            exactly the same no-seek-possible cost the JOIN-operand case reports, just reached
            through a WHERE clause rather than a JOIN's ON condition.

            Like its sibling, this is a structural fact from the catalog/AST (temp table has zero
            indexes at the point it's filtered), not a plan-shape guess - it fires regardless of the
            temp table's row count, though the cost scales with it.
            """,
        HowToFixIt: """
            Create an index on the #temp table covering the WHERE-filtered column(s).
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A populated #temp table filtered with no supporting index",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_LookupByCode AS
                    BEGIN
                        SELECT Id, Code INTO #t FROM dbo.Source WHERE Flag = 1;
                        SELECT * FROM #t WHERE Code = 'X';
                    END;
                    """,
                NoncompliantExplanation: "#t has no index on Code, so the WHERE Code = 'X' filter has no seek path - the engine scans every row of #t to evaluate it.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.usp_LookupByCode AS
                    BEGIN
                        SELECT Id, Code INTO #t FROM dbo.Source WHERE Flag = 1;
                        CREATE INDEX IX_t_Code ON #t (Code);
                        SELECT * FROM #t WHERE Code = 'X';
                    END;
                    """,
                CompliantExplanation: "The explicit CREATE INDEX on Code gives the engine a real seek path for the WHERE filter instead of forcing a full scan."),
        ]);
}
