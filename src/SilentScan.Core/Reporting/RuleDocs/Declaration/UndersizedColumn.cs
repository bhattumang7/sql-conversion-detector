using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Declaration;

internal static class UndersizedColumn
{
    public static string RuleId => SarifRuleCatalog.UndersizedDeclarationRuleId(UndersizedDeclarationSite.TableColumn);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A real table column declared as a string or binary type with a length of 1 or 2 -
            `VARCHAR(1)`, `NVARCHAR(2)`, `BINARY(1)`, and so on - is a code smell worth a second look
            on its own, needing no comparison against any other column to be worth flagging (unlike
            this codebase's separate under-length-vs-compared-column stream, which specifically
            checks a parameter or column against a real value it's compared/assigned against). A
            length this small is almost always one of two things: a value that was truncated down
            from a larger intended source during a copy-paste or a hasty first draft of a schema, or
            a leftover single-character flag/status placeholder ('Y'/'N', a status code) that later
            grew real string content nobody went back to widen the declaration for.

            This rule covers a table's real catalog columns specifically - the sibling
            `undersized-variable-or-parameter` rule covers the same length-1-or-2 shape for a
            DECLARE'd local variable or a procedure/function's own formal parameter instead. A temp
            table's or table variable's own column declarations (`CREATE TABLE #t(...)`, `SELECT
            ... INTO #t`, `DECLARE @t TABLE(...)`) are covered here too, for free, since the catalog
            already registers all three under the same table-column machinery other, earlier passes
            already rely on.

            This is purely an advisory/structural code-smell judgment call, not a provable runtime or
            plan-shape fact - reported at Low confidence, the same no-magnitude-claim tier this
            codebase's other purely-advisory findings use.
            """,
        HowToFixIt: """
            Confirm the column's real intended domain and widen its declared length if 1 or 2
            characters genuinely isn't enough for the data it's meant to hold; if it truly is a
            deliberate single-character flag, no change is needed.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A table column declared with a 1-character string length",
                NoncompliantSql: """
                    CREATE TABLE dbo.Customers
                    (
                        Id   INT NOT NULL PRIMARY KEY,
                        Name VARCHAR(1) NOT NULL
                    );
                    """,
                NoncompliantExplanation: "A customer's Name declared as VARCHAR(1) can hold exactly one character - almost certainly a truncated-from-a-larger-source mistake rather than a deliberate design choice for a name field.",
                CompliantSql: """
                    CREATE TABLE dbo.Customers
                    (
                        Id   INT NOT NULL PRIMARY KEY,
                        Name VARCHAR(100) NOT NULL
                    );
                    """,
                CompliantExplanation: "Widened to a length that can actually hold a real customer name."),
        ]);
}
