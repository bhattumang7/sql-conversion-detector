using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DeprecatedSyntax;

internal static class LegacyLobStatement
{
    public static string RuleId => SarifRuleCatalog.DeprecatedSyntaxRuleId(DeprecatedSyntaxFindingKind.LegacyLobStatement);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            READTEXT/WRITETEXT/UPDATETEXT are legacy statements for reading and writing the
            deprecated text/ntext/image large-object types in place, tracked by the engine's own
            deprecated-features counters. They only work against those three deprecated types, so
            any code still using them is coupled to a type family Microsoft has been recommending
            against since SQL Server 2005 and could remove outright in a future release.
            """,
        HowToFixIt: "Migrate the column to VARCHAR(MAX)/NVARCHAR(MAX)/VARBINARY(MAX) and use SUBSTRING/.WRITE()/UPDATE instead of READTEXT/WRITETEXT/UPDATETEXT.",
        Examples:
        [
            new RuleDocExample(
                Title: "UPDATETEXT against a text column",
                NoncompliantSql: """
                    DECLARE @ptr VARBINARY(16);
                    SELECT @ptr = TEXTPTR(Notes) FROM dbo.Ticket WHERE Id = 1;
                    UPDATETEXT dbo.Ticket.Notes @ptr 0 5 'HELLO';
                    """,
                NoncompliantExplanation: "UPDATETEXT only exists to patch a legacy text/ntext/image column in place and is tracked by the engine as a deprecated feature.",
                CompliantSql: "UPDATE dbo.Ticket SET Notes = STUFF(Notes, 1, 5, N'HELLO') WHERE Id = 1;",
                CompliantExplanation: "With the column stored as NVARCHAR(MAX), an ordinary UPDATE/STUFF() does the same in-place edit without the deprecated statement or its text-pointer plumbing."),
        ]);
}
