using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Tier1;

internal static class LeadingWildcardLike
{
    public static string RuleId => SarifRuleCatalog.Tier1RuleId(SargabilityFindingKind.LeadingWildcardLike);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A B-tree index on a string column is sorted by the column's value read left to right,
            character by character - exactly like a phone book sorted by last name. That structure
            lets the engine seek efficiently to any prefix: 'Smith%' can jump straight to where
            entries starting with "Smith" begin and scan forward only until they stop. But a
            pattern that starts with a wildcard - '%smith', '%son%' - gives the engine nothing to
            seek on, because there's no starting character to descend the B-tree toward. Every
            entry could potentially end in "smith" or contain "son" somewhere in the middle,
            regardless of what it starts with, so the engine has no choice but to scan every row
              and test the pattern against each one.

            This is a common and reasonable thing to want - "find every customer whose name
            contains 'son'" is a legitimate business question - but a plain LIKE with a leading
            wildcard can never answer it with an index seek, no matter what index exists on the
            column. The cost scales with table size exactly like a table scan, because that's
            functionally what it is.
            """,
        HowToFixIt: """
            There is no rewrite of a genuine leading-wildcard search that restores a seek on a
            standard B-tree index - the fix is a different indexing technology, not a different
            predicate. SQL Server's full-text index is built for exactly this: CONTAINS/FREETEXT
            predicates against a full-text index can answer substring and word-boundary searches
            without a full scan. If the search is really only ever a suffix match (find values
              ENDING in a known string), and the column's content is short and bounded, a computed,
            indexed REVERSE(column) column can turn it back into a prefix search
            (REVERSE(column) LIKE REVERSE('%suffix') becomes a prefix match on the reversed data).
            For a genuine "contains anywhere" search at scale, full-text search is the supported
            path.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A leading wildcard forces a full scan",
                NoncompliantSql: """
                    CREATE TABLE dbo.Customers
                    (
                        CustomerId INT          NOT NULL PRIMARY KEY,
                        Name       NVARCHAR(100) NOT NULL
                    );
                    CREATE INDEX IX_Customers_Name ON dbo.Customers(Name);

                    SELECT CustomerId
                    FROM dbo.Customers
                    WHERE Name LIKE '%son';
                    """,
                NoncompliantExplanation: "With no known starting character, the engine can't descend IX_Customers_Name's B-tree toward a useful starting point - every row is scanned and tested against the pattern."),
        ]);
}
