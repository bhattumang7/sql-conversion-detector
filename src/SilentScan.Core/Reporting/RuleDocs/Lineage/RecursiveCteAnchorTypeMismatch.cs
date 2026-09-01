using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Lineage;

internal static class RecursiveCteAnchorTypeMismatch
{
    public static string RuleId => SarifRuleCatalog.RecursiveCteAnchorTypeMismatchRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A recursive CTE has exactly one anchor member and one or more recursive members, joined
            by UNION ALL. T-SQL requires every recursive member's column list to resolve to exactly
            the anchor member's own type for that column - same category, same length/MAX-ness, same
            precision/scale, and (for string types) the same collation. Unlike a plain UNION, where
            mismatched operand types are silently reconciled through the usual implicit-conversion
            rules, a recursive CTE's own binder enforces an exact match and rejects anything else.

            This is a hard compile-time failure, confirmed directly against a real engine rather than
            assumed from documentation: even a length-only mismatch (VARCHAR(20) in the anchor against
            VARCHAR(5) in the recursive member) raises Msg 240, "Types don't match between the anchor
            and the recursive part in column ... of recursive query ...", and it fires before a single
            row is produced - a CREATE PROCEDURE or CREATE VIEW wrapping the mismatched CTE fails to
            compile at all, it is never deferred to first execution the way ordinary unresolved-name
            binding inside a module body is. Nullability plays no part in the comparison - only the
            declared type facets do.
            """,
        HowToFixIt: """
            Cast the recursive member's column to exactly the anchor member's own resolved type - same
            length/MAX, same precision/scale, and the same collation for a string column - so both
            members agree.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Recursive member narrows a VARCHAR column's length",
                NoncompliantSql: """
                    ;WITH Tree AS
                    (
                        SELECT CAST(CategoryCode AS VARCHAR(20)) AS CategoryCode, ParentCode
                        FROM dbo.Categories
                        WHERE ParentCode IS NULL
                        UNION ALL
                        SELECT CAST(c.CategoryCode AS VARCHAR(5)), c.ParentCode
                        FROM dbo.Categories c
                        JOIN Tree t ON c.ParentCode = t.CategoryCode
                    )
                    SELECT CategoryCode FROM Tree;
                    """,
                NoncompliantExplanation: "The anchor resolves CategoryCode to VARCHAR(20); the recursive member resolves the same column to VARCHAR(5) - the CTE fails to compile with Msg 240, before any row is produced.",
                CompliantSql: """
                    ;WITH Tree AS
                    (
                        SELECT CAST(CategoryCode AS VARCHAR(20)) AS CategoryCode, ParentCode
                        FROM dbo.Categories
                        WHERE ParentCode IS NULL
                        UNION ALL
                        SELECT CAST(c.CategoryCode AS VARCHAR(20)), c.ParentCode
                        FROM dbo.Categories c
                        JOIN Tree t ON c.ParentCode = t.CategoryCode
                    )
                    SELECT CategoryCode FROM Tree;
                    """,
                CompliantExplanation: "Both members resolve CategoryCode to exactly VARCHAR(20) - the types agree and the CTE compiles."),
        ]);
}
