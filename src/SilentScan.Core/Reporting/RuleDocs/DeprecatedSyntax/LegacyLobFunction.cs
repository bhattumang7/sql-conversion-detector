using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DeprecatedSyntax;

internal static class LegacyLobFunction
{
    public static string RuleId => SarifRuleCatalog.DeprecatedSyntaxRuleId(DeprecatedSyntaxFindingKind.LegacyLobFunction);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            TEXTPTR/TEXTVALID only exist to obtain and validate a text pointer for the deprecated
            text/ntext/image large-object types, tracked by the engine's own deprecated-features
            counters. Any code calling either function is coupled to a type family Microsoft has
            been recommending against since SQL Server 2005 and could remove outright in a future
            release.
            """,
        HowToFixIt: "Migrate the column to VARCHAR(MAX)/NVARCHAR(MAX)/VARBINARY(MAX) so the text pointer functions are no longer needed.",
        Examples:
        [
            new RuleDocExample(
                Title: "TEXTPTR feeding READTEXT",
                NoncompliantSql: """
                    DECLARE @ptr VARBINARY(16);
                    SELECT @ptr = TEXTPTR(Notes) FROM dbo.Ticket WHERE Id = 1;
                    READTEXT dbo.Ticket.Notes @ptr 0 5;
                    """,
                NoncompliantExplanation: "TEXTPTR only exists to support the legacy text/ntext/image family and is tracked by the engine as a deprecated feature.",
                CompliantSql: "SELECT SUBSTRING(Notes, 1, 5) FROM dbo.Ticket WHERE Id = 1;",
                CompliantExplanation: "With the column stored as NVARCHAR(MAX), SUBSTRING() reads the same range without a text pointer at all."),
        ]);
}
