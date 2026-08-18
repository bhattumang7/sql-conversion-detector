using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class UnionOfProvablyDisjointBranches
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.UnionOfProvablyDisjointBranches);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            UNION and UNION ALL both concatenate the row sets returned by their branches, but plain
            UNION additionally guarantees the combined result contains no duplicate rows - and it
            earns that guarantee by running a distinct-elimination step (implemented as a sort or a
            hash operation) over the entire combined row set after concatenation, comparing every
            row against every other row across all branches to find and remove duplicates. That
            work is real, engine-level cost: an extra operator in the plan, extra CPU, and for a
            sort-based implementation, an extra memory grant sized to the combined row count.

            That work exists to solve one specific problem: the same row appearing in more than one
            branch. When each branch already filters the same table or column to a distinct
            literal value - one branch is WHERE Status = 'Open', another is WHERE Status = 'Closed'
            - no row that satisfies one branch's filter can also satisfy another's, because Status
            can't simultaneously equal two different literals. The branches are provably mutually
            exclusive by construction, so no row from one branch can ever duplicate a row from
            another, and the distinct-elimination pass UNION performs is guaranteed to remove zero
            rows on every single run - it's paid for and never needed.

            UNION ALL performs the exact same concatenation without the distinct-elimination step:
            when the branches are provably disjoint, UNION ALL produces byte-for-byte the same
            result UNION would have, just without ever running the machinery that exists solely to
            catch duplicates that this particular shape can never produce.
            """,
        HowToFixIt: """
            Replace UNION with UNION ALL. This is safe specifically because the branches are
            provably disjoint on their filtering predicate - if a code change later loosens one
            branch's filter such that the branches could overlap, UNION ALL would then need to be
            revisited, but as written, no distinct-elimination pass is ever going to change the
            output.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "UNION of branches filtered to distinct literal values",
                NoncompliantSql: """
                    CREATE TABLE dbo.Tickets (TicketId INT NOT NULL PRIMARY KEY, Status VARCHAR(20) NOT NULL);

                    SELECT TicketId, Status FROM dbo.Tickets WHERE Status = 'Open'
                    UNION
                    SELECT TicketId, Status FROM dbo.Tickets WHERE Status = 'Closed';
                    """,
                NoncompliantExplanation: "No row can have Status equal to both 'Open' and 'Closed' at once, so the two branches can never produce an overlapping row - UNION still runs a full distinct-elimination pass over the combined result that can never actually remove anything.",
                CompliantSql: """
                    SELECT TicketId, Status FROM dbo.Tickets WHERE Status = 'Open'
                    UNION ALL
                    SELECT TicketId, Status FROM dbo.Tickets WHERE Status = 'Closed';
                    """,
                CompliantExplanation: "Same result, because the branches were already guaranteed disjoint - UNION ALL skips the distinct-elimination pass that UNION paid for without needing."),
        ]);
}
