using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ControlFlow;

internal static class LegacyIdentityIntrinsic
{
    public static string RuleId => SarifRuleCatalog.ControlFlowRiskRuleId(ControlFlowRiskFindingKind.LegacyIdentityIntrinsic);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `@@IDENTITY` returns the last identity value inserted in the CURRENT SESSION across ANY
            table and ANY scope - including a value inserted by a trigger that fired as a side
            effect of the very statement that ran just before this reference. That makes it a
            well-documented, sharp correctness trap: if the table just inserted into has a trigger
            that itself inserts into a different identity-bearing table, `@@IDENTITY` silently
            returns the WRONG value - the trigger's own inserted identity, not the one the calling
            code actually cares about - with no error raised anywhere to signal the mismatch.

            This pass cannot prove a trigger-caused collision is actually present for any specific
            `@@IDENTITY` reference - that would require knowing every trigger on the table being
            inserted into and whether any of them insert into another identity-bearing table. The
            finding is worded as "prefer `SCOPE_IDENTITY()` unless that broader session-wide
            semantics is specifically wanted," never as a claim that a bug is definitely present at
            this exact reference.
            """,
        HowToFixIt: """
            Use SCOPE_IDENTITY() instead, unless the broader session-wide (rather than
            current-scope) semantics of @@IDENTITY is specifically, deliberately wanted.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "@@IDENTITY after an INSERT into a table with an identity-inserting trigger",
                NoncompliantSql: """
                    INSERT INTO dbo.Orders (CustomerId) VALUES (@CustomerId);
                    SELECT @@IDENTITY;
                    """,
                NoncompliantExplanation: "If dbo.Orders has an AFTER INSERT trigger that itself inserts into a different identity-bearing table (e.g. an audit log), @@IDENTITY silently returns that trigger's own inserted identity instead of the Orders row's Id - no error, just the wrong value.",
                CompliantSql: """
                    INSERT INTO dbo.Orders (CustomerId) VALUES (@CustomerId);
                    SELECT SCOPE_IDENTITY();
                    """,
                CompliantExplanation: "SCOPE_IDENTITY() is scoped to the current session AND the current scope, so it returns the Orders row's own identity regardless of what any trigger fired as a side effect inserted elsewhere."),
        ]);
}
