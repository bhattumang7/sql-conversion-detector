using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ForcedParameterization;

internal static class DmlOutputListLiteral
{
    public static string RuleId => SarifRuleCatalog.ForcedParameterizationRuleId(ForcedParameterizationFindingKind.DmlOutputListLiteral);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Reported only when the target database has `PARAMETERIZATION FORCED` on. Confirmed
            directly against a real engine: `INSERT ... OUTPUT inserted.Id, 'tag' VALUES (...)`
            keeps the OUTPUT-list literal untouched in the cached plan while the VALUES clause's
            own literals correctly parameterize.

            A tag/label literal returned alongside `inserted`/`deleted` columns from an OUTPUT
            clause is a minor case in isolation, but it means that specific call site never shares
            a plan with a differently-tagged sibling insert/update/delete under
            PARAMETERIZATION FORCED.
            """,
        HowToFixIt: """
            Pass the OUTPUT-list literal as a parameter or local variable instead of a literal.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A literal tag in an OUTPUT clause",
                NoncompliantSql: """
                    INSERT INTO dbo.AuditLog (Id) OUTPUT inserted.Id, 'insert' VALUES (1);
                    """,
                NoncompliantExplanation: "Under PARAMETERIZATION FORCED, 'insert' stays literal in the cached plan - a sibling statement tagging 'update' compiles as a fully separate plan, not a shared one.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.InsertAuditRow @Id int, @Action varchar(20) AS
                    INSERT INTO dbo.AuditLog (Id) OUTPUT inserted.Id, @Action VALUES (@Id);
                    """,
                CompliantExplanation: "The tag is already a parameter, so every call - regardless of tag value - shares the one compiled plan."),
        ]);
}
