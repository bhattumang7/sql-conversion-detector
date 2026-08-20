using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class HeapWithNonclusteredPrimaryKey
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.HeapWithNonclusteredPrimaryKey);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The sharper sibling of `heap-with-nonclustered-indexes`: here the table has no clustered
            index anywhere because its own PRIMARY KEY constraint is itself declared NONCLUSTERED.
            This isn't an incidental gap - it's a specific, well-documented anti-pattern, because the
            single most commonly reached-for uniqueness guarantee on any table (its primary key) is
            the one that ends up guaranteed to cost a RID lookup on every nonclustered-index seek
            against that table, exactly the mechanism this whole rule family is concerned with.

            This is a catalog fact read directly from `sys.indexes` - live-mode only, the same as its
            general sibling rule.
            """,
        HowToFixIt: """
            Declare the PRIMARY KEY constraint CLUSTERED instead of NONCLUSTERED.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A primary key declared NONCLUSTERED, leaving the table a heap",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        Id     INT NOT NULL,
                        Status INT NOT NULL,
                        CONSTRAINT PK_Orders PRIMARY KEY NONCLUSTERED (Id)
                    );
                    """,
                NoncompliantExplanation: "The table's own primary key is nonclustered, so dbo.Orders has no clustered index anywhere - every other nonclustered index built on this table will pay the RID-lookup cost this whole rule family is about.",
                CompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        Id     INT NOT NULL,
                        Status INT NOT NULL,
                        CONSTRAINT PK_Orders PRIMARY KEY CLUSTERED (Id)
                    );
                    """,
                CompliantExplanation: "The primary key is now clustered, so the table has a real clustering key every other index can point back to."),
        ]);
}
