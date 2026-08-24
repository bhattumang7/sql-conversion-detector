using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Verdict;

internal static class CollationConflict
{
    public static string RuleId => SarifRuleCatalog.CollationConflictRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Two columns with genuinely different, incompatible collations are compared directly.
            This isn't a seek-vs-scan question the way most of this tool's other findings are - SQL
            Server refuses to run the statement at all, failing at compile/execute time with error
            468, "Cannot resolve the collation conflict between ... and ... in the equal to
            operation." The two columns typically ended up on different collations because they
            were created at different times under different database/instance default collations,
            or one was explicitly given a COLLATE clause the other never received. Nothing about the
            query's logic is wrong; the statement simply cannot execute as written.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Joining two columns with incompatible explicit collations",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, CustomerCode VARCHAR(20) COLLATE Latin1_General_CI_AS NOT NULL);
                    CREATE TABLE dbo.Customers (CustomerCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL PRIMARY KEY);

                    SELECT o.OrderId
                    FROM dbo.Orders o
                    JOIN dbo.Customers c ON o.CustomerCode = c.CustomerCode;
                    """,
                NoncompliantExplanation: "Orders.CustomerCode and Customers.CustomerCode carry two different, incompatible collations - the join predicate does not compile (error 468), regardless of what data either table holds.",
                CompliantSql: """
                    SELECT o.OrderId
                    FROM dbo.Orders o
                    JOIN dbo.Customers c ON o.CustomerCode = c.CustomerCode COLLATE Latin1_General_CI_AS;
                    """,
                CompliantExplanation: "An explicit COLLATE on one side forces both operands to a single, compatible collation, so the comparison compiles."),
        ]);
}
