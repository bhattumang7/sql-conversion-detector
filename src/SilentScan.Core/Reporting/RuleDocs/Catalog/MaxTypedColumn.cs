using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class MaxTypedColumn
{
    public static string RuleId => SarifRuleCatalog.MaxTypedColumnRuleId(NonIndexableColumnFindingKind.MaxLength);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `VARCHAR(MAX)`, `NVARCHAR(MAX)`, and `VARBINARY(MAX)` aren't just "a very large length" -
            they're a structurally different storage class from their bounded counterparts, and that
            difference has a hard consequence the optimizer can't work around: a MAX-typed column can
            never be a key column in any index at all. SQL Server's index key is limited to 900 bytes
            for a nonclustered index (1,700 bytes if the index is unique and includes only bounded
            columns) - a limit rooted in the B-tree page size - and a MAX type's actual data is
            typically stored off-row once it exceeds the in-row threshold, which makes it
            structurally ineligible to serve as a key regardless of how short the values stored in it
            happen to be in practice. A column declared VARCHAR(MAX) that in every row actually holds
            a 12-character code is still permanently barred from being an index key, because the
            engine goes by the declared type, not the observed data.

            The practical consequence is that any predicate or join on a MAX-typed column can never
            seek, full stop - not "can seek if the right index exists," but structurally cannot,
            because no index can exist with this column as a key. This is a purely catalog-derived
            structural fact (the column's declared type, read straight from `sys.columns`), true the
            moment the column is declared this way, independent of any query - the finding exists to
            flag that ceiling before someone spends time trying to index their way out of a slow
            predicate against a column that was never eligible to be seekable in the first place.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A short, frequently-filtered code column declared MAX-typed",
                NoncompliantSql: """
                    CREATE TABLE dbo.Products
                    (
                        ProductId  INT             NOT NULL PRIMARY KEY,
                        SkuCode    VARCHAR(MAX)    NOT NULL
                    );
                    -- Every SkuCode value is actually 8-12 characters, but no index can ever be
                    -- built with SkuCode as a key column - MAX types can't be index keys at all.

                    SELECT ProductId FROM dbo.Products WHERE SkuCode = 'SKU-001234';
                    """,
                NoncompliantExplanation: "SkuCode's actual content is short, but its declared type is VARCHAR(MAX) - the engine goes by the declared type, so no index can ever have SkuCode as a key column, and this predicate can never seek regardless of how it's written.",
                CompliantSql: """
                    CREATE TABLE dbo.Products
                    (
                        ProductId  INT           NOT NULL PRIMARY KEY,
                        SkuCode    VARCHAR(20)    NOT NULL
                    );
                    CREATE INDEX IX_Products_SkuCode ON dbo.Products(SkuCode);

                    SELECT ProductId FROM dbo.Products WHERE SkuCode = 'SKU-001234';
                    """,
                CompliantExplanation: "With a bounded length that actually reflects the real data, SkuCode is eligible to be an index key, and IX_Products_SkuCode makes this predicate seekable. Genuinely unbounded content that's never filtered or joined on directly - a document body, say - is correctly left MAX-typed; this finding is purely informational in that case."),
        ]);
}
