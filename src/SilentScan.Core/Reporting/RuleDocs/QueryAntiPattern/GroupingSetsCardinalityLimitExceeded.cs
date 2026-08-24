using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class GroupingSetsCardinalityLimitExceeded
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.GroupingSetsCardinalityLimitExceeded);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            CUBE, ROLLUP, and explicit GROUPING SETS all expand a GROUP BY clause into more than
            one underlying grouping set, and the engine caps how many grouping sets a single
            clause can expand into before it will even compile the statement. This scan's oracle
            confirms two distinct, fixed ceilings, both purely syntactic and independent of the
            table's real data:

            The first is the total expanded grouping-set count across the whole clause, capped at
            4096. CUBE(...) expands its column list into every subset, so a CUBE over more than 12
            columns already exceeds 4096 combinations (2^13 = 8192) on its own; an explicit
            GROUPING SETS(...) list hits the same 4096 ceiling once the combinations its own
            entries expand to (a CUBE or ROLLUP nested inside one of its sets counts for as many
            combinations as it would standalone) add up past it. Either way the statement is
            rejected outright with Msg 10703, "Too many grouping sets. The maximum number is
            4096."

            The second is a tighter limit on the total number of grouping expressions named
            anywhere in an extended GROUP BY clause (one using CUBE, ROLLUP, or GROUPING SETS),
            capped at 32. ROLLUP(...) never has enough columns to hit the 4096 combination ceiling
            in practice - it only ever adds one combination per column - so this 32-expression
            ceiling is the one that actually rejects an oversized ROLLUP, with Msg 10706, "Too
            many expressions are specified in the GROUP BY clause. The maximum number is 32 when
            grouping sets are supplied." Both ceilings apply equally to the older
            WITH CUBE/WITH ROLLUP syntax appended after a plain column list.

            Neither ceiling has anything to do with how much data the query actually touches, and
            both are usually reached by tables that simply have a lot of dimension columns, not by
            malformed queries - a wide fact table rolled up across all of its columns, or a report
            that lists every combination of a dozen or more attributes, can cross either limit
            with no warning until the statement is actually executed and fails to compile.
            """,
        HowToFixIt: """
            Reduce the number of columns passed to CUBE or ROLLUP, or the number of sets listed in
            GROUPING SETS, until the clause is back under the engine's fixed limit. If every
            combination genuinely needs to be produced, split the aggregation into multiple
            statements (e.g. several smaller ROLLUP/CUBE/GROUPING SETS clauses unioned together)
            instead of asking a single clause to expand past what the engine allows.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A CUBE over more than 12 columns",
                NoncompliantSql: """
                    SELECT c1, c2, c3, c4, c5, c6, c7, c8, c9, c10, c11, c12, c13, COUNT(*)
                    FROM dbo.WideFact
                    GROUP BY CUBE(c1, c2, c3, c4, c5, c6, c7, c8, c9, c10, c11, c12, c13);
                    """,
                NoncompliantExplanation: "13 columns expand to 2^13 = 8192 grouping sets, past the engine's fixed 4096 ceiling - the statement does not compile (Msg 10703), regardless of how much data dbo.WideFact actually holds.",
                CompliantSql: """
                    SELECT c1, c2, c3, c4, c5, c6, c7, c8, c9, c10, c11, c12, COUNT(*)
                    FROM dbo.WideFact
                    GROUP BY CUBE(c1, c2, c3, c4, c5, c6, c7, c8, c9, c10, c11, c12);
                    """,
                CompliantExplanation: "12 columns expand to exactly 2^12 = 4096 grouping sets, the engine's own boundary - the statement compiles."),
        ]);
}
