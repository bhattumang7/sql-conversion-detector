using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class FullTextIndexTooManyColumns
{
    public static string RuleId => SarifRuleCatalog.FullTextIndexDdlRuleId(FullTextIndexDdlFindingKind.TooManyIndexedColumns);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A single CREATE FULLTEXT INDEX statement's column list carries more than the documented
            1,024-column limit for a full-text index. An ordinary table can never itself exceed 1,024
            columns, but a wide table using sparse columns and a column set can carry far more, so the
            limit is reachable in practice even though it can never be hit by an ordinary table's
            column count alone. This is purely a count of the statement's own column list - no
            catalog or live connection needed.
            """,
        HowToFixIt: """
            Split the indexed columns across fewer, narrower full-text indexes (a table can carry
            only one full-text index, so this also means reducing which columns need full-text search
            at all), or drop columns from the list that don't actually need it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A full-text index column list past the documented limit",
                NoncompliantSql: """
                    -- dbo.WideNotes is a wide table (WITH (DATA_COMPRESSION = ...) / column set)
                    -- carrying 1,100 sparse NVARCHAR columns.
                    CREATE FULLTEXT INDEX ON dbo.WideNotes(Col0001, Col0002, /* ... */ Col1100)
                        KEY INDEX PK__WideNotes;
                    """,
                NoncompliantExplanation: "The column list carries more than 1,024 columns, over the documented full-text index limit."),
        ]);
}
