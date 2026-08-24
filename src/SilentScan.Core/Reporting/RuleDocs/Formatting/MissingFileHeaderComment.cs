using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Formatting;

internal static class MissingFileHeaderComment
{
    public static string RuleId => SarifRuleCatalog.FormattingRuleId(FormattingFindingKind.MissingFileHeaderComment);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A module's own definition does not begin with a comment before its first real statement.
            Purely advisory - T-SQL modules carry no universal file-header convention the way
            application source files do.
            """,
        HowToFixIt: """
            Add a leading comment before the module's first statement if the team's convention calls
            for one, documenting its purpose, ownership, or change history.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A procedure with no header comment",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.GetActiveOrders
                    AS
                    BEGIN
                        SELECT OrderId FROM dbo.Orders WHERE Status = 'Active';
                    END
                    """,
                NoncompliantExplanation: "The procedure body begins immediately with no comment describing its purpose or ownership.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.GetActiveOrders
                    -- Returns the id of every order currently in the Active status.
                    AS
                    BEGIN
                        SELECT OrderId FROM dbo.Orders WHERE Status = 'Active';
                    END
                    """,
                CompliantExplanation: "A leading comment documents the procedure's purpose before its first statement."),
        ]);
}
