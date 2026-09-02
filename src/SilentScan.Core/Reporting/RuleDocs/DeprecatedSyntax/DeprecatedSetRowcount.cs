using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DeprecatedSyntax;

internal static class DeprecatedSetRowcount
{
    public static string RuleId => SarifRuleCatalog.DeprecatedSyntaxRuleId(DeprecatedSyntaxFindingKind.DeprecatedSetRowcount);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SET ROWCOUNT silently caps rows affected/returned by every subsequent statement in the
            session right now, not just as a future-deprecation risk: oracle-confirmed, a nonzero
            SET ROWCOUNT left active before a multi-row INSERT makes a later SELECT COUNT(*) against
            that same data return the capped count, not the true one. It is also deprecated - use
            TOP (n) instead; Microsoft documents it as not honored by INSERT/UPDATE/DELETE in a
            future release. Code relying on it to limit rows affected by a DML statement is at risk
            of silently losing that limit once the deprecation lands, on top of the present-tense
            risk of a forgotten SET ROWCOUNT quietly limiting unrelated statements later in the same
            session.
            """,
        HowToFixIt: "Use TOP (n) instead of SET ROWCOUNT.",
        Examples:
        [
            new RuleDocExample(
                Title: "SET ROWCOUNT limiting a DELETE",
                NoncompliantSql: """
                    SET ROWCOUNT 100;
                    DELETE FROM dbo.OrderStaging WHERE Status = 'Processed';
                    SET ROWCOUNT 0;
                    """,
                NoncompliantExplanation: "SET ROWCOUNT is deprecated and Microsoft documents it as not honored by DELETE in a future release, so the row limit could silently stop applying.",
                CompliantSql: "DELETE TOP (100) FROM dbo.OrderStaging WHERE Status = 'Processed';",
                CompliantExplanation: "TOP (n) expresses the same row limit directly on the statement, with no session-level setting to forget to reset."),
        ]);
}
