using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class RandomClusteredKeyGuidDefault
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.RandomClusteredKeyGuidDefault);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A clustered index whose leading (or sole) key column is `uniqueidentifier`-typed and
            defaults to `NEWID()` is one of the most well-documented SQL Server anti-patterns there
            is: `NEWID()` generates values in genuinely random order, so every insert lands at a
            random point in the clustered B-tree instead of at the end where sequential inserts
            naturally land - causing severe page splits and fragmentation as the engine constantly
            has to make room in the middle of already-full pages, rather than simply appending.

            `NEWSEQUENTIALID()` is the precision-guarding near-miss this rule must never fire on: it
            generates values that increase sequentially (not fully ordered across a server restart,
            but monotonic within one boot cycle), which avoids the random-insert problem entirely.
            The scanner matches by exact default-text equality after stripping whitespace and
            parentheses, never a substring match - verified directly that a naive substring check
            would be wrong here, since "NEWID(" is technically a substring of "NEWSEQUENTIALID()" and
            a careless match would misfire on the very function meant to avoid this problem.
            """,
        HowToFixIt: """
            Default the uniqueidentifier column to NEWSEQUENTIALID() instead of NEWID().
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A clustered GUID key defaulted to NEWID()",
                NoncompliantSql: """
                    CREATE TABLE dbo.Sessions
                    (
                        SessionId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
                        StartedAt DATETIME2 NOT NULL,
                        CONSTRAINT PK_Sessions PRIMARY KEY CLUSTERED (SessionId)
                    );
                    """,
                NoncompliantExplanation: "NEWID() generates genuinely random values, so every insert lands at a random point in the clustered B-tree instead of at the end - severe page splits and fragmentation as the table grows.",
                CompliantSql: """
                    CREATE TABLE dbo.Sessions
                    (
                        SessionId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
                        StartedAt DATETIME2 NOT NULL,
                        CONSTRAINT PK_Sessions PRIMARY KEY CLUSTERED (SessionId)
                    );
                    """,
                CompliantExplanation: "NEWSEQUENTIALID() generates values that increase monotonically within a boot cycle, so inserts land at the end of the B-tree like a sequential key would, avoiding the random-insert fragmentation problem."),
        ]);
}
