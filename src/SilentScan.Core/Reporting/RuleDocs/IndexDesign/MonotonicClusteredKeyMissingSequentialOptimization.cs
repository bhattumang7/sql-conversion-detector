using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class MonotonicClusteredKeyMissingSequentialOptimization
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.MonotonicClusteredKeyMissingSequentialOptimization);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            This is the precise mirror image of the sibling `random-clustered-key-guid-default`
            rule: where a random clustering key fragments the whole clustered B-tree, a
            monotonically increasing one hotspots a single trailing page instead - every insert
            lands immediately after the last row, so under concurrent load, inserts can serialize
            on that one page's latch rather than spreading across the tree.

            Scoped to the clear, high-confidence case only: the clustered index's leading key column
            is an `IDENTITY` column with a positive increment (a negative or zero increment is not
            "always-ascending" and is deliberately excluded rather than guessed about - broadening
            to other monotonic-by-construction patterns, like a sequence-defaulted column or an
            ever-increasing datetime default, was evaluated and not done, since this pass has no
            cheap, precise way to prove a non-IDENTITY column is monotonic from the catalog alone
            without risking a false positive). `OPTIMIZE_FOR_SEQUENTIAL_KEY` (a real, current
            index-level mitigation, confirmed directly against a real engine, originally shipped in
            SQL Server 2019 CU5) is checked directly from the live catalog, so this finding never
            fires against an index that already carries the mitigation.

            Shipped as a structural risk flag only, the same discipline as the sibling columnstore
            rule: the structural precondition is catalog-decidable, but whether it actually causes
            contention depends on concurrent insert rate - real workload data this pass cannot see
            and never claims to know.
            """,
        HowToFixIt: """
            Enable OPTIMIZE_FOR_SEQUENTIAL_KEY on the clustered index.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A clustered IDENTITY key with no sequential-key optimization",
                NoncompliantSql: """
                    CREATE TABLE dbo.EventLog
                    (
                        Id BIGINT IDENTITY(1,1) NOT NULL,
                        Message NVARCHAR(500) NOT NULL,
                        CONSTRAINT PK_EventLog PRIMARY KEY CLUSTERED (Id)
                    );
                    """,
                NoncompliantExplanation: "Every insert lands immediately after the highest existing Id - under high concurrent insert volume (a busy event log is a classic case), inserts can serialize on that one trailing page's latch.",
                CompliantSql: """
                    ALTER INDEX PK_EventLog ON dbo.EventLog SET (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON);
                    """,
                CompliantExplanation: "OPTIMIZE_FOR_SEQUENTIAL_KEY specifically mitigates last-page-insert contention for a monotonically increasing clustered key, without changing the key itself."),
        ]);
}
