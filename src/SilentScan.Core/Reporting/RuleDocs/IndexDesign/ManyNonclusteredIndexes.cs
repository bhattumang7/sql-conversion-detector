using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class ManyNonclusteredIndexes
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.ManyNonclusteredIndexes);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A table carrying at least 7 active nonclustered indexes has real write cost multiplying
            with every one: each index is maintained on every INSERT/UPDATE/DELETE that touches the
            table, whether or not that particular index is ever actually used by a query. This is
            deliberately a lower-precision, threshold-based finding than the rest of this rule
            family's structurally-provable kinds (`duplicate-index`, `subsumed-index`, and similar) -
            it reports only the raw fact that the table "carries N indexes, each paid for on every
            write," never "drop this specific one." Which index (if any) is actually safe to drop
            needs real production usage statistics - which index a query actually reads, and how
            often - that this catalog-only pass structurally cannot see.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A table with many nonclustered indexes",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders (Id INT NOT NULL PRIMARY KEY, /* ...many columns... */ A INT, B INT, C INT, D INT, E INT, F INT, G INT);
                    CREATE INDEX IX_Orders_A ON dbo.Orders(A);
                    CREATE INDEX IX_Orders_B ON dbo.Orders(B);
                    CREATE INDEX IX_Orders_C ON dbo.Orders(C);
                    CREATE INDEX IX_Orders_D ON dbo.Orders(D);
                    CREATE INDEX IX_Orders_E ON dbo.Orders(E);
                    CREATE INDEX IX_Orders_F ON dbo.Orders(F);
                    CREATE INDEX IX_Orders_G ON dbo.Orders(G);
                    """,
                NoncompliantExplanation: "Seven active nonclustered indexes each get maintained on every write to dbo.Orders - a real, ongoing write cost regardless of how often each individual index is actually read by a query."),
        ]);
}
