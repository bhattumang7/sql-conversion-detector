using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Duplication;

internal static class DuplicatedStringLiteral
{
    public static string RuleId => SarifRuleCatalog.DuplicationRuleId(DuplicationFindingKind.DuplicatedStringLiteral);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The same non-trivial string literal appears three or more times within one module - a
            magic value that should be a variable or constant instead. Repeating the literal means a
            future change to its value has to be made correctly in every occurrence.
            """,
        HowToFixIt: "Extract the repeated string literal into a variable or constant instead of repeating it.",
        Examples:
        [
            new RuleDocExample(
                Title: "The same string literal repeated across a module",
                NoncompliantSql: """
                    SELECT OrderId FROM dbo.Orders WHERE Status = 'PendingReview';
                    UPDATE dbo.Orders SET Status = 'PendingReview' WHERE OrderId = @orderId;
                    DELETE FROM dbo.OrderStaging WHERE Status = 'PendingReview';
                    """,
                NoncompliantExplanation: "'PendingReview' is repeated three times - if the status value ever changes, every occurrence has to be updated correctly.",
                CompliantSql: """
                    DECLARE @pendingReviewStatus NVARCHAR(20) = 'PendingReview';
                    SELECT OrderId FROM dbo.Orders WHERE Status = @pendingReviewStatus;
                    UPDATE dbo.Orders SET Status = @pendingReviewStatus WHERE OrderId = @orderId;
                    DELETE FROM dbo.OrderStaging WHERE Status = @pendingReviewStatus;
                    """,
                CompliantExplanation: "The value now lives in one variable, so a future change only needs to happen in one place."),
        ]);
}
