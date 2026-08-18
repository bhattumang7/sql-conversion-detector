using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class AnsiPaddingMismatch
{
    public static string RuleId => SarifRuleCatalog.AnsiPaddingMismatchRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            ANSI_PADDING is a session/database setting that controls whether trailing spaces on a
            varchar or varbinary value are preserved when the value is stored. With ANSI_PADDING
            ON - the modern default, and the only setting new connections generally use - trailing
            whitespace in a varchar value is preserved exactly as supplied. With ANSI_PADDING OFF, a
            legacy setting that predates the ANSI SQL-92 standard becoming the default and is now
            deprecated, trailing spaces are stripped from a varchar/varbinary value at INSERT or
            UPDATE time, before the value is ever stored. This is a storage-time transformation, not
            a comparison-time one - once a column was populated under ANSI_PADDING OFF, the trailing
            whitespace is simply gone from what's on disk; there is no way for any later query to
            find it, because it was never kept.

            This becomes a genuine, silent correctness gap for a LIKE predicate whose pattern has
            significant trailing whitespace: LIKE 'ABC ' (with a trailing space that's part of the
            match, not incidental formatting) can only ever match a stored value that itself ends in
            that space. If the column can never contain such a value - because every value was
            written while ANSI_PADDING was OFF and had its trailing whitespace stripped before
            storage - the pattern is not just unlikely to match, it's structurally guaranteed to
            never match anything the column could ever hold, for as long as that storage condition
            held. This is a data-semantics finding, not a plan-shape one: it has nothing to do with
            whether an index can be seeked, and everything to do with the predicate being unable to
            find anything by construction.
            """,
        HowToFixIt: """
            The pattern is comparing against trailing whitespace the column can never actually
            contain, so either stop relying on trailing whitespace in the search pattern at all -
            trim it from the pattern, since it can never contribute to a match - or, if trailing
            whitespace genuinely needs to be preserved and searched for on this column going
            forward, ensure ANSI_PADDING is ON (the modern default) for the session/connection that
            writes to it, so future values actually retain what the pattern is trying to match.
            Values already stored under ANSI_PADDING OFF have already lost their trailing
            whitespace permanently; turning ANSI_PADDING back ON only affects writes from that point
            forward, not what's already on disk.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A LIKE pattern with trailing whitespace against an ANSI_PADDING OFF column",
                NoncompliantSql: """
                    SET ANSI_PADDING OFF;

                    CREATE TABLE dbo.Codes
                    (
                        CodeId INT         NOT NULL PRIMARY KEY,
                        Code   VARCHAR(10) NOT NULL
                    );

                    INSERT INTO dbo.Codes (CodeId, Code) VALUES (1, 'ABC   ');

                    SELECT CodeId
                    FROM dbo.Codes
                    WHERE Code LIKE 'ABC   ';
                    """,
                NoncompliantExplanation: "With ANSI_PADDING OFF, the trailing spaces on 'ABC   ' are stripped before the row is stored - Code actually holds 'ABC'. The LIKE pattern's trailing spaces can never match a value the column was ever able to store, so the query returns nothing even though a row with that exact literal was just inserted.",
                CompliantSql: """
                    SELECT CodeId
                    FROM dbo.Codes
                    WHERE Code LIKE 'ABC';
                    """,
                CompliantExplanation: "With the trailing whitespace trimmed from the pattern, the predicate now compares against what the column can actually contain under ANSI_PADDING OFF, and matches the stored 'ABC' row."),
        ]);
}
