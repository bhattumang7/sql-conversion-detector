using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DynamicSql;

internal static class Unanalyzable
{
    public static string RuleId => SarifRuleCatalog.DynamicSqlUnanalyzableRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An `EXEC(string)`/`EXEC(@sql)`/`sp_executesql` call site whose argument depends on a
            variable, parameter, or expression this tool cannot trace to a provably-constant value -
            tracing what the value would be at runtime would mean guessing, which this codebase
            never does. This is the single most common outcome for real dynamic SQL, and it's
            reported honestly rather than silently skipped: CLAUDE.md's own dynamic-SQL policy is
            explicit that "anything not provably constant is reported with a machine-readable reason
            and counted in `DynamicSqlSummary` - never silently counted as clean." A call site this
            tool can't see into is a real gap in its own coverage, and that gap is itself the
            finding.

            Every `Unanalyzable` finding carries a specific, machine-readable reason (e.g.
            `variable-not-in-scope`) - never a generic "couldn't analyze this" - so the actual
            obstacle is visible rather than opaque. This is purely a coverage-honesty report, not a
            claim that the dynamic SQL is itself wrong or dangerous in any way (the SECURITY-framed
            `unprovable-dynamic-sql-text` finding covers that separate, actionable angle for the
            identical population of call sites).
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A dynamic SQL call site whose variable has no traceable value",
                NoncompliantSql: """
                    EXEC(@sql);
                    """,
                NoncompliantExplanation: "There is no DECLARE or procedure parameter for @sql anywhere in scope - its value is genuinely unknowable from the code alone, so this tool reports it honestly as Unanalyzable (reason: variable-not-in-scope) rather than guessing or silently skipping it."),
        ]);
}
