using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class VariableLengthKeyColumnExceedsKeyLimit
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.VariableLengthKeyColumnExceedsKeyLimit);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An index key's combined declared max byte width - every key column, fixed-length ones
            included - can exceed the engine's own real key-length ceiling - 900 bytes for a
            clustered index, primary key, or unique constraint's own key; 1700 bytes for a
            nonclustered index's own key, both exact figures confirmed directly against a real
            engine's own warning text, not assumed or taken from documentation alone. This applies
            even when no single column is individually over the limit: two `varchar(500)` key
            columns (1000 combined bytes) or an `int` plus a `varchar(898)` key column (902 combined
            bytes) both exceed the 900-byte clustered ceiling on the sum alone.

            The dangerous part is what happens next: `CREATE INDEX` does NOT fail when the
            declared-max combined width exceeds the limit and at least one key column is
            `varchar`/`nvarchar`/`varbinary` (non-MAX) - it only prints a warning, one easily
            swallowed by deployment tooling that doesn't surface SQL warnings. The index gets
            created successfully and works fine until, sometimes years later in production, an
            `INSERT` or `UPDATE` finally stores values long enough to actually exceed the real limit
            - only THEN does it fail (Msg 1946, "Operation failed... exceeds the maximum length"),
            silently until that moment. This is a genuinely different, more dangerous shape than a
            key whose fixed-length columns (`char`/`nchar`/`binary`) alone already sum past the same
            ceiling, which the engine already refuses to compile at `CREATE INDEX` time (Msg
            1944/1946-family) - that case needs no rule, since the engine's own compile-time error
            already catches it.
            """,
        HowToFixIt: """
            Shorten the key's variable-length column(s), or narrow another key column, so the
            combined declared max width stays under the engine's limit (900 bytes clustered, 1700
            nonclustered).
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
            new RuleDocExample(
                Title: "Two clustered key columns, neither individually over the limit, whose sum is",
                NoncompliantSql: """
                    CREATE TABLE dbo.Docs (CodeA VARCHAR(500) NOT NULL, CodeB VARCHAR(500) NOT NULL, CONSTRAINT PK_Docs PRIMARY KEY CLUSTERED (CodeA, CodeB));
                    """,
                NoncompliantExplanation: "Neither VARCHAR(500) column exceeds the 900-byte clustered ceiling on its own, but their combined 1000 declared bytes does - CREATE INDEX only warns and succeeds, so the failure is silently deferred to whatever future INSERT/UPDATE first stores combined values long enough to actually exceed it.",
                CompliantSql: """
                    CREATE TABLE dbo.Docs (CodeA VARCHAR(400) NOT NULL, CodeB VARCHAR(400) NOT NULL, CONSTRAINT PK_Docs PRIMARY KEY CLUSTERED (CodeA, CodeB));
                    """,
                CompliantExplanation: "The combined 800 declared bytes is safely under the 900-byte clustered ceiling - no deferred failure risk."),
        ]);
}
