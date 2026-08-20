using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Security;

internal static class HardCodedCredential
{
    public static string RuleId => SarifRuleCatalog.SecurityRuleId(SecurityFindingKind.HardCodedCredential);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A local variable or parameter whose own name suggests it holds a credential (matched
            against a small, independently-chosen word list - password/passwd/pwd/secret) assigned a
            literal string value directly in the module's own source text means that value now lives
            everywhere this module's text does: in source control history, in every backup of the
            database that stores it, and in every copy of the script this tool itself reads to
            produce this finding. A credential belongs in a secrets store or external configuration,
            never embedded where it outlives any single deployment.

            Name-based matching always carries real false-positive risk - a variable named
            `@passwordHash` could legitimately be assigned a literal display placeholder, or a name
            containing "pwd" as a mid-word substring (a real one caught during this tool's own
            testing: `@VehInOpWD`, "Operating WeekDays") isn't a credential at all. For that reason
            this finding is reported at Low confidence - a lead worth checking, not a confirmed
            finding - and the scanner requires the suspicious word to appear as a whole word in the
            name, not merely as a substring.
            """,
        HowToFixIt: """
            Keep the credential in a secrets store or external configuration instead of hard-coding
            it in source text.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A credential-named variable assigned a literal value",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_ConnectToLinkedSystem AS
                    BEGIN
                        DECLARE @Password VARCHAR(50) = 'hunter2';
                        -- ... use @Password to authenticate ...
                    END;
                    """,
                NoncompliantExplanation: "The literal 'hunter2' is embedded directly in the procedure's own source text, so it persists in source control, backups, and every copy of this script - not a secrets store.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.usp_ConnectToLinkedSystem
                        @Password VARCHAR(50)
                    AS
                    BEGIN
                        -- ... use @Password, supplied by the caller from a secrets store ...
                    END;
                    """,
                CompliantExplanation: "The credential is now supplied by the caller at runtime (ideally sourced from a real secrets store) rather than embedded as a literal in the procedure's own text."),
        ]);
}
