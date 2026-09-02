using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class SemanticSearchTableNotIndexed
{
    public static string RuleId => SarifRuleCatalog.SemanticSearchRuleId(SemanticSearchFindingKind.TableNotSemanticFullTextIndexed);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SEMANTICKEYPHRASETABLE, SEMANTICSIMILARITYTABLE, and SEMANTICSIMILARITYDETAILSTABLE all
            require their source table to carry a full-text index with at least one column enabled
            via the STATISTICAL_SEMANTICS option - an ordinary full-text index that only supports
            CONTAINS/FREETEXT does not qualify. Oracle-confirmed (Msg 41202, "doesn't have a
            full-text index that uses the STATISTICAL_SEMANTICS option"): the call fails at
            execution whenever the table has no full-text index at all, or has one without any
            semantically indexed column and the call leaves the column argument as * rather than
            naming one explicitly (naming a specific column on a table that does have a full-text
            index surfaces as the sibling column-level rule instead). This is a catalog-only fact,
            read straight from the live database's own full-text metadata
            (sys.fulltext_index_columns) at scan time.
            """,
        HowToFixIt: """
            Enable STATISTICAL_SEMANTICS on at least one full-text indexed column of the source
            table (CREATE FULLTEXT INDEX ... WITH (... STATISTICAL_SEMANTICS) or ALTER FULLTEXT
            INDEX ... ADD (column STATISTICAL_SEMANTICS ...)), or stop calling the semantic search
            function against a table that was never meant to support it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Semantic search against a table with only an ordinary full-text index",
                NoncompliantSql: """
                    SELECT d.DocumentId, k.keyphrase, k.score
                    FROM dbo.Documents AS d
                    CROSS APPLY SEMANTICKEYPHRASETABLE(dbo.Documents, *, d.DocumentId) AS k;
                    """,
                NoncompliantExplanation: "dbo.Documents has a full-text index on Body, but none of its columns are enabled with STATISTICAL_SEMANTICS, so this fails with error 41202.",
                CompliantSql: """
                    ALTER FULLTEXT INDEX ON dbo.Documents ALTER COLUMN Body
                        ADD STATISTICAL_SEMANTICS;

                    SELECT d.DocumentId, k.keyphrase, k.score
                    FROM dbo.Documents AS d
                    CROSS APPLY SEMANTICKEYPHRASETABLE(dbo.Documents, *, d.DocumentId) AS k;
                    """,
                CompliantExplanation: "Enabling STATISTICAL_SEMANTICS on Body makes it a qualifying semantically indexed column, so the call succeeds."),
        ]);
}
