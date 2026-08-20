using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class NonUniqueClusteredIndex
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.NonUniqueClusteredIndex);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A clustered index (rowstore) that is not declared unique has an invisible cost that
            multiplies across every other index on the table: every nonclustered index carries a
            copy of the clustering key in its own leaf rows, as the row locator it uses to find the
            base row. When the clustering key permits duplicates, the engine silently adds a hidden
            4-byte "uniquifier" to every duplicate-keyed row to keep those row locators unique
            internally - extra storage and extra key width that never appears in the table's own
            declared schema, paid on every single row and multiplied across every other index built
            on the table.

            This is a catalog fact read directly from `sys.indexes` (`is_unique = 0`) - live-mode
            only, since there's no file-mode equivalent of asking whether an index is actually
            unique without a real connected target.
            """,
        HowToFixIt: """
            Make the clustered index's key unique (or add a uniquifying column) so the engine
            doesn't add its own hidden 4-byte uniquifier.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A clustered index with no uniqueness guarantee",
                NoncompliantSql: """
                    CREATE TABLE dbo.EventLog
                    (
                        LoggedAt DATETIME2 NOT NULL,
                        Message  VARCHAR(200) NOT NULL
                    );
                    CREATE CLUSTERED INDEX IX_EventLog_LoggedAt ON dbo.EventLog(LoggedAt);
                    -- LoggedAt is not guaranteed unique - two events can log at the same instant.
                    """,
                NoncompliantExplanation: "Whenever two rows share the same LoggedAt value, the engine adds a hidden 4-byte uniquifier to tell them apart internally - extra width silently carried in every nonclustered index this table ever gets.",
                CompliantSql: """
                    CREATE TABLE dbo.EventLog
                    (
                        Id       INT IDENTITY NOT NULL,
                        LoggedAt DATETIME2 NOT NULL,
                        Message  VARCHAR(200) NOT NULL
                    );
                    CREATE UNIQUE CLUSTERED INDEX IX_EventLog_LoggedAt_Id ON dbo.EventLog(LoggedAt, Id);
                    """,
                CompliantExplanation: "Adding the identity column to the key makes the clustered index genuinely unique, so no hidden uniquifier is ever needed."),
        ]);
}
