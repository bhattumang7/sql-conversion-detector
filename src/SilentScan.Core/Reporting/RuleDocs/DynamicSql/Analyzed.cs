using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DynamicSql;

internal static class Analyzed
{
    public static string RuleId => SarifRuleCatalog.DynamicSqlAnalyzedRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            This is the success case of this tool's dynamic-SQL pipeline, not a defect report: an
            `EXEC(string)`/`EXEC(@sql)`/`sp_executesql` call site whose argument was PROVABLY
            constant (a literal, or a concatenation of bare literals with no variable/parameter/
            expression in the mix) had its reassembled text reparsed and run back through this
            tool's own normal analysis pipeline, exactly as if it had been written as static SQL in
            the first place - every other rule this tool ships can fire against the reconstructed
            query, with findings remapped back to this call site as their provenance.

            This finding exists specifically so a dynamic-SQL call site's outcome is never left
            implicit. CLAUDE.md's own dynamic-SQL policy is that every call site's resolution is
            reported honestly, one way or another, in `DynamicSqlSummary` - `Analyzed` is the
            "this one worked, here's what happened to it" record, the counterpart to
            `Unanalyzable`/`InnerParseFailed`/`PartiallyAnalyzed` reporting the ways it can't. It's
            reported as a SARIF Note (informational), not a Warning - there's nothing here to fix,
            only a record of what this tool was able to see through.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A dynamic SQL call site whose argument is a bare literal",
                NoncompliantSql: """
                    DECLARE @sql NVARCHAR(MAX) = N'SELECT Id, Name FROM dbo.Products WHERE Active = 1';
                    EXEC(@sql);
                    """,
                NoncompliantExplanation: "@sql's value is a single literal assignment with no variable/parameter/expression feeding into it - the reassembled text was successfully reparsed and analyzed exactly like ordinary static SQL, and this finding simply records that outcome. (\"Noncompliant\" only in the structural sense of \"this is dynamic SQL, not static\" - there is nothing to fix here.)"),
        ]);
}
