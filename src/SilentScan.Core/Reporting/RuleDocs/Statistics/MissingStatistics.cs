using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Statistics;

internal static class MissingStatistics
{
    public static string RuleId => SarifRuleCatalog.MissingStatisticsRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A predicate resolves to a real base table column that no statistic - neither a
            single-column statistic, nor a multi-column statistic with this column as its own
            leading key - covers, while the connected database has automatic statistics creation
            turned off (AUTO_CREATE_STATISTICS OFF). With auto-create on, the engine would normally
            create the missing single-column statistic itself the first time the predicate compiles;
            with it off, that safety net doesn't exist, and the optimizer is left estimating this
            predicate's selectivity from nothing but guesswork, which routinely produces a bad plan
            with no error or warning surfaced anywhere. A non-leading occurrence of the column in a
            multi-column statistic does not count as coverage here - oracle-confirmed: the engine
            still auto-creates its own single-column statistic for such a column when auto-create is
            on, meaning the multi-column statistic alone genuinely isn't equivalent coverage.
            """,
        HowToFixIt: """
            Create a statistic covering this column explicitly (CREATE STATISTICS), or turn
            AUTO_CREATE_STATISTICS back on for the database so the engine can create one itself.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A predicate column with no covering statistic under AUTO_CREATE_STATISTICS OFF",
                NoncompliantSql: """
                    ALTER DATABASE CURRENT SET AUTO_CREATE_STATISTICS OFF;

                    CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, Status VARCHAR(20) NOT NULL);

                    SELECT OrderId
                    FROM dbo.Orders
                    WHERE Status = 'Pending';
                    """,
                NoncompliantExplanation: "No statistic on Status exists, and auto-create is off - the optimizer has no distribution data to estimate this predicate's selectivity from and cannot create one itself.",
                CompliantSql: """
                    CREATE STATISTICS Stat_Orders_Status ON dbo.Orders(Status);

                    SELECT OrderId
                    FROM dbo.Orders
                    WHERE Status = 'Pending';
                    """,
                CompliantExplanation: "An explicit statistic on Status gives the optimizer real distribution data for this predicate, independent of AUTO_CREATE_STATISTICS."),
        ]);
}
