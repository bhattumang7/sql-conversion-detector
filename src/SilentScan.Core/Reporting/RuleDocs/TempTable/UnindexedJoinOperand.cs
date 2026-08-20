using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.TempTable;

internal static class UnindexedJoinOperand
{
    public static string RuleId => SarifRuleCatalog.UnindexedTempTableUsageRuleId(UnindexedTempTableUsageKind.JoinOperand);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A `#temp` table built via `SELECT ... INTO` gets no indexes of its own by default -
            unlike a real base table where a primary key or explicit index is usually declared up
            front, the temp table's structure comes entirely from whatever the SELECT list produced,
            and nothing about `SELECT INTO` creates a supporting index automatically. When that temp
            table is later used as a JOIN operand in the same batch or procedure and no `CREATE
            INDEX` statement appears anywhere between the population and the join, there is no seek
            alternative available at all - oracle-confirmed directly: the engine has no choice but a
            full scan (typically materializing into a Hash Match) of the entire temp table's
            contents for every execution.

            This is a purely structural, always-true cost claim, not a plan-shape guess: the finding
            fires from the catalog/AST fact that the temp table has zero indexes at the point it's
            joined, which by itself rules out any seek regardless of row counts, statistics, or
            which specific join algorithm the optimizer ultimately picks. It gets more expensive as
            the temp table grows, but it's a real cost even for a small one, since a scan-driven
            Hash Match is real CPU and memory grant work.
            """,
        HowToFixIt: """
            Create an index on the #temp table before using it as a JOIN operand, covering the
            column(s) the join predicate compares against.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A populated #temp table joined with no supporting index",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_MatchOrders AS
                    BEGIN
                        SELECT Id, Code INTO #t FROM dbo.Source WHERE Flag = 1;
                        SELECT s.* FROM dbo.Source2 s INNER JOIN #t t ON s.Code = t.Code;
                    END;
                    """,
                NoncompliantExplanation: "#t has no index at all by the time it's joined on Code - the engine has no way to seek into it, so the join forces a full scan of #t's entire contents on every execution.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.usp_MatchOrders AS
                    BEGIN
                        SELECT Id, Code INTO #t FROM dbo.Source WHERE Flag = 1;
                        CREATE INDEX IX_t_Code ON #t (Code);
                        SELECT s.* FROM dbo.Source2 s INNER JOIN #t t ON s.Code = t.Code;
                    END;
                    """,
                CompliantExplanation: "The explicit CREATE INDEX on the join column gives the engine a real seek path into #t instead of forcing a full scan."),
        ]);
}
