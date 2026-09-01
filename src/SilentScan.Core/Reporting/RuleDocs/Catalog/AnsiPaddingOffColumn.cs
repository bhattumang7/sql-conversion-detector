using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class AnsiPaddingOffColumn
{
    public static string RuleId => SarifRuleCatalog.ColumnAnsiPaddingOffRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Every `VARCHAR`/`NVARCHAR`/`VARBINARY` column carries its own `ANSI_PADDING` state
            (`sys.columns.is_ansi_padded`), and that state is a permanent, one-time snapshot taken
            when the column is created (or last had `ALTER COLUMN` run against it) - whichever
            `SET ANSI_PADDING` setting happened to be in effect for that one statement. It is not a
            live link to any session setting from that point on.

            With `ANSI_PADDING` OFF, the column silently strips trailing blanks from character
            values and trailing zero bytes from `VARBINARY` values at write time - and it keeps
            doing this regardless of what any later session's own `SET ANSI_PADDING` is set to.
            A developer who runs `SET ANSI_PADDING ON` before an `INSERT`, expecting trailing
            whitespace to survive, gets no error and no warning: the value is silently trimmed
            anyway, because the column's own recorded state - not the session's - governs. Nothing
            in a `SELECT *` or a casual schema browse makes this difference visible; it only shows
            up as data that doesn't round-trip the way it was written, or (for `LIKE` patterns with
            trailing whitespace) a predicate that can never match anything the column could ever
            contain. Detecting the column's own OFF state from the catalog alone flags the risk
            before any query or insert reaches it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A column created while ANSI_PADDING was OFF keeps trimming forever",
                NoncompliantSql: """
                    SET ANSI_PADDING OFF;
                    CREATE TABLE dbo.Codes (Code VARCHAR(20) NOT NULL);
                    """,
                NoncompliantExplanation: "Code's own is_ansi_padded catalog state is permanently OFF from creation - every future INSERT/UPDATE into it silently strips trailing blanks, even one issued from a session running SET ANSI_PADDING ON.",
                CompliantSql: """
                    SET ANSI_PADDING ON;
                    CREATE TABLE dbo.Codes (Code VARCHAR(20) NOT NULL);
                    """,
                CompliantExplanation: "Code's own is_ansi_padded catalog state is ON - trailing blanks are preserved exactly as written, matching what every session expects by default."),
        ]);
}
