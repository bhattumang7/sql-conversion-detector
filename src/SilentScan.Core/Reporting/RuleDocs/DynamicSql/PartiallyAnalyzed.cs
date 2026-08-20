using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DynamicSql;

internal static class PartiallyAnalyzed
{
    public static string RuleId => SarifRuleCatalog.DynamicSqlPartiallyAnalyzedRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A dynamic SQL call site's argument contained a symbolic value standing for a WHOLE
            optional clause or fragment - not a single scalar value - typically a variable spliced
            in to conditionally add something like an extra `AND` filter. No identifier-shaped
            placeholder token can sit in that position without breaking the reparse (a clause
            fragment isn't a legal expression), so this tool substitutes a single space instead: a
            substitution that can never accidentally fuse two adjacent literal fragments together
            the way deleting the span outright could, and that reveals a valid statement missing
            only the part this tool could never see anyway.

            Findings are reported for everything that was ALREADY fully present in the static
            text - the surrounding, unaffected query structure genuinely is analyzed, and every
            other rule this tool ships can fire against it. The elided fragment's own content is
            never guessed at, so this outcome can only ever under-report relative to the true
            runtime query, never fabricate a claim about what the missing fragment contains. This
            is the same "report what you can prove, stay honest about the rest" discipline as
            `Unanalyzable`, applied to a partial rather than total gap in coverage.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A dynamic SQL call site with an elided optional filter fragment",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_Events
                        @FilterFragment NVARCHAR(100)
                    AS
                    BEGIN
                        DECLARE @sql NVARCHAR(MAX) =
                            N'SELECT Id FROM dbo.Events e ' + @FilterFragment + N' ORDER BY Id DESC';
                        EXEC(@sql);
                    END;
                    """,
                NoncompliantExplanation: "@FilterFragment stands for a whole optional clause (empty, or something like ' AND EventType = 5') rather than a single scalar value - this tool analyzes the surrounding structure it CAN see (the SELECT/FROM/ORDER BY), reports the call site as PartiallyAnalyzed, and never guesses at what the elided fragment itself might contain."),
        ]);
}
