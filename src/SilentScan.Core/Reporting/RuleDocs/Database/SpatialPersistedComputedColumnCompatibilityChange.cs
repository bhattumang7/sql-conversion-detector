using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Database;

internal static class SpatialPersistedComputedColumnCompatibilityChange
{
    public static string RuleId => SarifRuleCatalog.DatabaseConfigurationRuleId(DatabaseConfigurationFindingKind.SpatialPersistedComputedColumnDisabledOnCompatibilityLevelChange);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SQL Server reports this exact index from `sys.dm_db_objects_disabled_on_compatibility_level_change` for the connected instance's own current default compatibility level. The DMV identifies indexes that depend on a persisted computed column using a `geography` or `geometry` method and which the engine will disable during that compatibility-level change.

            The finding is live-only because the DMV is the authoritative implementation of the compatibility rule. SilentScan does not attempt to reconstruct or generalize the engine's eligibility logic from computed-column text.
            """,
        HowToFixIt: """
            Replace the persisted computed-column spatial expression or plan to rebuild the affected index after validating the compatibility-level change.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An indexed persisted spatial computation reported by SQL Server",
                NoncompliantSql: """
                    CREATE TABLE dbo.Areas
                    (
                        Id INT NOT NULL CONSTRAINT PK_Areas PRIMARY KEY,
                        Location geography NOT NULL,
                        ComparisonLocation geography NOT NULL,
                        Distance AS (Location.STDistance(ComparisonLocation)) PERSISTED,
                        Buffered AS (Location.STBuffer(1)) PERSISTED
                    );
                    CREATE INDEX IX_Areas_Distance ON dbo.Areas(Distance);
                    CREATE SPATIAL INDEX SIX_Areas_Location ON dbo.Areas(Location) USING GEOGRAPHY_GRID;
                    """,
                NoncompliantExplanation: "When the target compatibility level reaches the connected instance's current default, SQL Server's DMV reports this dependent index as disabled by that change.",
                CompliantSql: null,
                CompliantExplanation: null),
        ]);
}
