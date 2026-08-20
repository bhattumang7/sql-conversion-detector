using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class VariableLengthKeyColumnExceedsKeyLimit
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.VariableLengthKeyColumnExceedsKeyLimit);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A `varchar`/`nvarchar`/`varbinary` (non-MAX) index key column's declared max byte width
            can exceed the engine's own real key-length ceiling - 900 bytes for a clustered index,
            primary key, or unique constraint's own key; 1700 bytes for a nonclustered index's own
            key, both exact figures confirmed directly against a real engine's own warning text,
            not assumed or taken from documentation alone.

            The dangerous part is what happens next: `CREATE INDEX` does NOT fail when the
            declared-max width exceeds the limit - it only prints a warning, one easily swallowed by
            deployment tooling that doesn't surface SQL warnings. The index gets created
            successfully and works fine until, sometimes years later in production, an `INSERT` or
            `UPDATE` finally stores a value long enough to actually exceed the real limit - only THEN
            does it fail (Msg 1946, "Operation failed... exceeds the maximum length"), silently
            until that moment. This is a genuinely different, more dangerous shape than a
            fixed-length type (`char`/`nchar`/`binary`) over the same ceiling, which the engine
            already refuses to compile at `CREATE INDEX` time (Msg 1944/1946-family) - that case
            needs no rule, since the engine's own compile-time error already catches it.
            """,
        HowToFixIt: """
            Shorten the column's declared max length so the index key stays under the engine's limit
            (900 bytes clustered, 1700 nonclustered).
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A nonclustered key column declared wider than the 1700-byte ceiling",
                NoncompliantSql: """
                    CREATE TABLE dbo.Docs (Notes NVARCHAR(900) NOT NULL);
                    CREATE NONCLUSTERED INDEX IX_Docs_Notes ON dbo.Docs (Notes);
                    """,
                NoncompliantExplanation: "NVARCHAR(900) is 1800 declared bytes (2 bytes per character), over the 1700-byte nonclustered key ceiling - CREATE INDEX only warns and succeeds, so the failure is silently deferred to whatever future INSERT/UPDATE first stores a value long enough to actually exceed it.",
                CompliantSql: """
                    CREATE TABLE dbo.Docs (Notes NVARCHAR(450) NOT NULL);
                    CREATE NONCLUSTERED INDEX IX_Docs_Notes ON dbo.Docs (Notes);
                    """,
                CompliantExplanation: "NVARCHAR(450) is 900 declared bytes, safely under the 1700-byte nonclustered ceiling - no deferred failure risk."),
        ]);
}
