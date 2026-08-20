using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Join;

internal static class CartesianCommaJoin
{
    public static string RuleId => SarifRuleCatalog.CartesianJoinRuleId(CartesianJoinKind.CommaJoin);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The legacy comma-join syntax (`FROM A, B`) has no dedicated place to write a join
            predicate - any connecting condition has to live in the WHERE clause instead, which
            means leaving it out is a single missing line, not a missing clause the syntax itself
            calls attention to. When no predicate anywhere in the statement - no WHERE condition, no
            join elsewhere in the FROM list - connects two of the comma-separated tables, SQL Server
            executes exactly what was written: a true cartesian product, every row of one table
            paired with every row of the other.

            This is the classic "forgot the join condition" defect, and it's dangerous specifically
            because it still runs and still returns rows. On small development tables a cartesian
            product might return a plausible-looking few dozen rows and pass a cursory review; the
            same query against production-sized tables returns a row count in the millions or
            billions, multiplying out every downstream aggregate and very possibly exhausting tempdb
            or the query's memory grant before anyone notices the row count is wrong rather than
            just slow.

            This rule checks connectivity as a graph property across the whole FROM clause, not a
            pairwise one: three tables can lack any direct predicate between two of them while still
            being transitively joined through a shared third table, and that's correctly not flagged
            - only a FROM clause whose connectivity graph has more than one component is a genuine
            cartesian defect. It also declines entirely (reports nothing) if any predicate in the
            statement references an unqualified column, since attributing an unqualified reference to
            one side of the join would risk a false negative rather than a safe decline.
            """,
        HowToFixIt: """
            Add a join predicate - either an explicit ON-style condition rewritten as ANSI JOIN
            syntax, or a WHERE condition connecting the two tables - so every table in the FROM list
            is reachable from every other one through some real predicate chain.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Two comma-joined tables with no connecting predicate",
                NoncompliantSql: """
                    SELECT o.OrderId, c.CustomerName
                    FROM dbo.Orders AS o, dbo.Customers AS c
                    WHERE o.Status = 'Open';
                    """,
                NoncompliantExplanation: "Nothing anywhere in the statement equates a column of Orders to a column of Customers - o.Status = 'Open' filters Orders alone, so every open order is paired with every customer, a cartesian product between the two tables.",
                CompliantSql: """
                    SELECT o.OrderId, c.CustomerName
                    FROM dbo.Orders AS o, dbo.Customers AS c
                    WHERE o.Status = 'Open' AND o.CustomerId = c.CustomerId;
                    """,
                CompliantExplanation: "The added o.CustomerId = c.CustomerId condition connects the two tables, so each open order is matched only to its own customer."),
        ]);
}
