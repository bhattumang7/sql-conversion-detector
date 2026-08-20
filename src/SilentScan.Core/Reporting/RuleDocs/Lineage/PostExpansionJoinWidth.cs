using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Lineage;

internal static class PostExpansionJoinWidth
{
    public static string RuleId => SarifRuleCatalog.PostExpansionJoinWidthRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Counting tables in a query's own written `FROM`/`JOIN` list is meaningless once any of
            those sources is a view - a query that looks like a simple three-table join can expand
            to twenty real base tables once every view/inline-TVF reference is resolved
            transitively. This rule reports the EXPANDED count, computed via this tool's own
            lineage pass resolving every view/TVF reference down to real base tables - a number no
            purely syntactic table-count tool can ever see, since it requires actually knowing what
            each referenced view or function's own definition touches.

            Findings are ranked by the gap between the expanded count and the written count, firing
            once that gap reaches 3 or more - this catches exactly the "looks small, is actually
            large" case even when the absolute expanded count itself isn't enormous. This rule
            deliberately does NOT claim a specific "past N tables the optimizer gives up exhaustive
            join-order search" threshold - that number is real but unconfirmed folklore this
            codebase's own precision discipline declines to assert without a real plan-XML
            confirmation that hasn't been run yet. The counting mechanism itself needs no such
            confirmation, since it's exact structural arithmetic over an already-verified lineage
            pass.

            When some FROM-clause reference can't be expanded further (a derived table, an
            MSTVF/CLR TVF fence, an unmodeled dynamic construct), the expanded count is reported as
            a lower bound, never claimed as exhaustive - this tool's consistent "never guess" rule
            applied to counting, not just to type inference.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A one-table query expanding to five real base tables through a view",
                NoncompliantSql: """
                    CREATE VIEW dbo.vWide AS
                        SELECT T1.Id FROM dbo.T1
                        JOIN dbo.T2 ON T1.Id = T2.Id
                        JOIN dbo.T3 ON T1.Id = T3.Id
                        JOIN dbo.T4 ON T1.Id = T4.Id
                        JOIN dbo.T5 ON T1.Id = T5.Id;

                    SELECT Id FROM dbo.vWide;
                    """,
                NoncompliantExplanation: "The written FROM clause names exactly one table (dbo.vWide) - but that view's own definition joins five real base tables together, so this query's real expanded width is 5, a gap of 4 from what the written text shows."),
        ]);
}
