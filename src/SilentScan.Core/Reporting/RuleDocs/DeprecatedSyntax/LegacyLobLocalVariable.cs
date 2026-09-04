using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DeprecatedSyntax;

internal static class LegacyLobLocalVariable
{
    public static string RuleId => SarifRuleCatalog.DeprecatedSyntaxRuleId(DeprecatedSyntaxFindingKind.LegacyLobLocalVariable);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Confirmed directly against a real SQL Server instance: `DECLARE @x TEXT` (or
            `NTEXT`/`IMAGE`) fails to compile with Msg 2739 ("The text, ntext, and image data types
            are invalid for local variables.") every time, regardless of whether the variable is
            ever assigned or read. This is unconditional - unlike most of this scanner's other
            findings, it is not a style/deprecation warning about code that still runs; the batch,
            procedure, or function containing the declaration never compiles at all.

            Procedure and function parameters of these types remain legal - only local variable
            declarations are rejected.
            """,
        HowToFixIt: "Declare the variable as VARCHAR(MAX)/NVARCHAR(MAX)/VARBINARY(MAX) instead.",
        Examples:
        [
            new RuleDocExample(
                Title: "A local TEXT variable never compiles",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.GetNotes
                    AS
                    BEGIN
                        DECLARE @notes TEXT;
                        SELECT @notes = Notes FROM dbo.Ticket WHERE Id = 1;
                        SELECT @notes;
                    END;
                    """,
                NoncompliantExplanation: "DECLARE @notes TEXT fails to compile with Msg 2739 - the procedure body never parses successfully.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.GetNotes
                    AS
                    BEGIN
                        DECLARE @notes NVARCHAR(MAX);
                        SELECT @notes = Notes FROM dbo.Ticket WHERE Id = 1;
                        SELECT @notes;
                    END;
                    """,
                CompliantExplanation: "NVARCHAR(MAX) is legal for a local variable and holds the same range of content."),
        ]);
}
