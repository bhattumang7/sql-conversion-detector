using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class TableVariableStaleEstimateInLoop
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.TableVariableStaleEstimateInLoop);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A single statement that reads a table variable is compiled once and its plan is
            typically reused for the lifetime of that compiled batch - table variables don't
            trigger the automatic recompilation that a #temp table's statistics changes do, because
            (below deferred compilation) they carry no statistics to become stale in the first
            place, and even with deferred compilation the deferral only applies to the very first
            compile. Inside a WHILE loop that both writes to a table variable and reads from it on
            every iteration, this becomes a moving target: the read statement is compiled once,
            against whatever the table variable looked like the first time that statement executed,
            and every later iteration reuses that same plan and the same cardinality estimate even
            as the table variable keeps growing.

            This scan's oracle confirms the freeze directly against plan XML rather than assuming
            it from the loop shape: the estimated row count recorded for the read operator stays
              pinned to the size the table variable had at (or near) the first iteration, while the
            actual row count climbs with each pass through the loop. A loop that inserts one row
            per iteration and reads the accumulated table variable back on iteration 500 is still
            compiled against something close to a 1-row (or single-iteration) estimate, so the
            optimizer keeps choosing a plan shaped for a handful of rows - typically nested loops -
            long after the table variable holds enough rows that a different plan would be faster.

            The practical effect is a query that gets progressively slower relative to its own
            estimate as the loop runs, without ever triggering a recompile to correct it, because
            nothing about a table variable's growth is a statistics-invalidation event the way it
            is for a permanent or temp table.
            """,
        HowToFixIt: """
            Replace the table variable with a #temp table for any loop that both grows it and reads
            it back for a cost-sensitive operation: #temp tables carry real statistics, and SQL
            Server automatically recompiles a statement once the underlying temp table's row count
            has changed enough to invalidate the cached plan, so later iterations get a fresh
            estimate instead of reusing the first iteration's guess. Where switching object types
            isn't practical, forcing a recompile of the read statement on each iteration (for
            example with `OPTION (RECOMPILE)`) achieves the same effect at the cost of repeated
            compilation overhead every pass through the loop.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A table variable grown and read inside the same loop",
                NoncompliantSql: """
                    CREATE TABLE dbo.SourceRows (RowId INT NOT NULL PRIMARY KEY, Amount DECIMAL(10,2) NOT NULL);
                    CREATE TABLE dbo.Batches (BatchTotal DECIMAL(18,2) NOT NULL);

                    DECLARE @Accumulator TABLE (RowId INT NOT NULL, Amount DECIMAL(10,2) NOT NULL);
                    DECLARE @NextRowId INT = 1;

                    WHILE @NextRowId <= 1000
                    BEGIN
                        INSERT INTO @Accumulator (RowId, Amount)
                        SELECT RowId, Amount FROM dbo.SourceRows WHERE RowId = @NextRowId;

                        INSERT INTO dbo.Batches (BatchTotal)
                        SELECT SUM(Amount) FROM @Accumulator;

                        SET @NextRowId += 1;
                    END;
                    """,
                NoncompliantExplanation: "The SELECT SUM(Amount) FROM @Accumulator statement is compiled once, against @Accumulator's size at the first iteration - by iteration 1000 the table variable holds 1000 rows but the cached plan is still estimating for the handful of rows present when it first compiled.",
                CompliantSql: """
                    CREATE TABLE #Accumulator (RowId INT NOT NULL, Amount DECIMAL(10,2) NOT NULL);
                    DECLARE @NextRowId INT = 1;

                    WHILE @NextRowId <= 1000
                    BEGIN
                        INSERT INTO #Accumulator (RowId, Amount)
                        SELECT RowId, Amount FROM dbo.SourceRows WHERE RowId = @NextRowId;

                        INSERT INTO dbo.Batches (BatchTotal)
                        SELECT SUM(Amount) FROM #Accumulator;

                        SET @NextRowId += 1;
                    END;
                    """,
                CompliantExplanation: "#Accumulator's statistics update as it grows, and SQL Server recompiles the SELECT SUM(...) statement once that growth crosses the recompilation threshold, so later iterations get an estimate that reflects the temp table's actual current size."),
        ]);
}
