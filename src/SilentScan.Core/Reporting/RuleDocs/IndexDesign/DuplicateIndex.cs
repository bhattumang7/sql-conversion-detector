using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class DuplicateIndex
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.DuplicateIndex);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Two active (non-disabled) indexes on the same table whose ordered key-column lists are
            exactly identical, with the same uniqueness and index kind between them, are real,
            deterministic duplicates - not merely similar indexes, genuinely redundant objects. One
            of the pair serves no query the other doesn't already serve equally well; it exists
            purely as write amplification (every INSERT/UPDATE/DELETE maintains both copies) and
            wasted storage, with zero query benefit over keeping just one.

            This comparison deliberately declines two precision-risk cases rather than guess: a
            filtered index is never compared this way at all, since this pass reads only whether an
            index IS filtered, not the filter predicate's own text - two filtered indexes can never
            be confirmed identical here, so they're excluded rather than falsely matched. Neither is
            a columnstore index ever compared, since it has no ordered B-tree key the same way a
            rowstore index does.
            """,
        HowToFixIt: """
            Drop one of the two exact-duplicate indexes.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Two nonclustered indexes with the identical key",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders (Id INT NOT NULL, Status INT NOT NULL);
                    CREATE NONCLUSTERED INDEX IX_Orders_Status ON dbo.Orders(Status);
                    CREATE NONCLUSTERED INDEX IX_Orders_Status_Dup ON dbo.Orders(Status);
                    """,
                NoncompliantExplanation: "Both indexes carry the identical key column (Status), the same uniqueness (neither is unique), and the same index kind - IX_Orders_Status_Dup serves no query the first index doesn't already serve, while every write still has to maintain both.",
                CompliantSql: """
                    CREATE TABLE dbo.Orders (Id INT NOT NULL, Status INT NOT NULL);
                    CREATE NONCLUSTERED INDEX IX_Orders_Status ON dbo.Orders(Status);
                    """,
                CompliantExplanation: "Dropping the exact duplicate removes the redundant write cost with no loss of query capability."),
        ]);
}
