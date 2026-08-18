using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Tier1;

internal static class CharindexOrLeftOnColumn
{
    public static string RuleId => SarifRuleCatalog.Tier1RuleId(SargabilityFindingKind.CharindexOrLeftOnColumn);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            CHARINDEX(x, col) and LEFT(col, n) are both function wraps around the column, so both
            defeat an index seek exactly like any other wrapped-column pattern - but this rule
            treats them specially because, unlike a genuine substring search, some uses of these
            two functions are actually asking a prefix question in disguise, and a prefix question
            has an exact sargable rewrite available.

            CHARINDEX(x, col) = 1 asks "does col start with x" - CHARINDEX returns the 1-based
            position of the first match, and requiring that position to be exactly 1 means the
            match has to be at the very start of the string. That's precisely what col LIKE 'x%'
            asks too, with the difference that LIKE with a trailing (not leading) wildcard IS
            seekable, per the LeadingWildcardLike rule's own logic in reverse. Similarly,
            LEFT(col, n) = 'x' where LEN('x') = n is asking "do the first n characters of col equal
            x" - again exactly a prefix question, again rewritable as col LIKE 'x%'.

            Any other use of CHARINDEX or LEFT against a column - CHARINDEX(x, col) > 0 (does col
            contain x anywhere), CHARINDEX(x, col) = 5 (does the match start at position 5
            specifically), LEFT(col, n) = 'x' where LEN('x') != n - is a genuine substring or
            fixed-position search with no equivalent LIKE rewrite, and needs full-text search or a
            computed/indexed column to become sargable, the same as any other genuine substring
            search.
            """,
        HowToFixIt: """
            For the prefix-match shape specifically - CHARINDEX(x, col) = 1, or LEFT(col, n) = 'x'
            where LEN('x') = n - rewrite as col LIKE 'x%'. The rewrite is exact, not an
            approximation: both forms match exactly the rows whose column value starts with x, and
            the LIKE form is fully seekable. For any other shape (a genuine substring search, or a
            fixed non-1 starting position), there is no equivalent LIKE rewrite - the same
            full-text-search or computed-column approach LeadingWildcardLike's own fix guidance
            describes applies here too.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "CHARINDEX(...) = 1 is really a prefix match",
                NoncompliantSql: """
                    CREATE TABLE dbo.Products
                    (
                        ProductId INT          NOT NULL PRIMARY KEY,
                        Code      NVARCHAR(50) NOT NULL
                    );
                    CREATE INDEX IX_Products_Code ON dbo.Products(Code);

                    SELECT ProductId
                    FROM dbo.Products
                    WHERE CHARINDEX('AB-', Code) = 1;
                    """,
                NoncompliantExplanation: "CHARINDEX(...) is computed per row before the comparison, so IX_Products_Code can't be seeked, even though the question being asked is really just a prefix match.",
                CompliantSql: """
                    SELECT ProductId
                    FROM dbo.Products
                    WHERE Code LIKE 'AB-%';
                    """,
                CompliantExplanation: "The exact same rows, expressed as a prefix LIKE pattern - Code is now bare, and the optimizer can seek IX_Products_Code."),
            new RuleDocExample(
                Title: "A genuine substring search has no sargable rewrite",
                NoncompliantSql: """
                    SELECT ProductId
                    FROM dbo.Products
                    WHERE CHARINDEX('AB', Code) > 0;
                    """,
                NoncompliantExplanation: "Asking whether 'AB' appears ANYWHERE in Code (not necessarily at the start) is a genuine substring search - there is no LIKE-prefix rewrite that means the same thing, so this stays a full scan under a standard B-tree index. A full-text index (CONTAINS/FREETEXT) is the supported way to make this kind of search seekable at scale."),
        ]);
}
