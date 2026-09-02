using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class SemanticSearchColumnNotIndexed
{
    public static string RuleId => SarifRuleCatalog.SemanticSearchRuleId(SemanticSearchFindingKind.ColumnNotSemanticFullTextIndexed);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SEMANTICKEYPHRASETABLE, SEMANTICSIMILARITYTABLE, and SEMANTICSIMILARITYDETAILSTABLE all
            take a source (and, for SEMANTICSIMILARITYDETAILSTABLE, a matched) column argument, and
            that specific column must itself be full-text indexed with STATISTICAL_SEMANTICS - it is
            not enough for some other column on the same table to qualify. Oracle-confirmed (Msg
            41203, "must be full-text indexed using the STATISTICAL_SEMANTICS option"): the call
            fails at execution even when the table does have a semantically indexed column, if the
            one actually named isn't it. This is a catalog-only fact, read straight from the live
            database's own full-text metadata (sys.fulltext_index_columns) at scan time.
            """,
        HowToFixIt: """
            Name the column that is actually enabled with STATISTICAL_SEMANTICS, or enable the
            option on the column being referenced (ALTER FULLTEXT INDEX ... ALTER COLUMN column_name
            ADD STATISTICAL_SEMANTICS).
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Semantic search naming a column that isn't the semantically indexed one",
                NoncompliantSql: """
                    -- Body has STATISTICAL_SEMANTICS enabled; Summary does not.
                    SELECT d.DocumentId, k.keyphrase, k.score
                    FROM dbo.Documents AS d
                    CROSS APPLY SEMANTICKEYPHRASETABLE(dbo.Documents, Summary, d.DocumentId) AS k;
                    """,
                NoncompliantExplanation: "Summary exists and is full-text indexed, but STATISTICAL_SEMANTICS is only enabled on Body, so naming Summary here fails with error 41203.",
                CompliantSql: """
                    SELECT d.DocumentId, k.keyphrase, k.score
                    FROM dbo.Documents AS d
                    CROSS APPLY SEMANTICKEYPHRASETABLE(dbo.Documents, Body, d.DocumentId) AS k;
                    """,
                CompliantExplanation: "Body is the column enabled with STATISTICAL_SEMANTICS, so naming it here succeeds."),
        ]);
}
