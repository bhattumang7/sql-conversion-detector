using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Lineage;

/// <summary>
/// A recursive CTE with TWO recursive members (three total UNION ALL branches: one anchor, two
/// recursive) parses left-associatively - ((anchor UNION ALL recursive1) UNION ALL recursive2) -
/// so the anchor sits two levels deep inside the outer BinaryQueryExpression's own First side,
/// not as a direct child. <see cref="CteResolver.ResolveRecursiveAnchor"/> only inspected the
/// outer BinaryQueryExpression's two direct children, so for this real, valid T-SQL shape it
/// picked the SECOND recursive member (still self-referencing) as if it were the anchor, instead
/// of ever finding the true anchor buried in the nested First side.
/// </summary>
public sealed class RecursiveCteThreeBranchTests
{
    private static ResolvedRelation Build(string ddl, string viewSql)
    {
        var sql = ddl + "\nGO\n" + viewSql;
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        var lineage = LineageResolver.Resolve(catalog, [result]);
        return lineage.Find("dbo.vw_Tree")!;
    }

    [Fact]
    public void RecursiveCteWithTwoRecursiveMembers_ResolvesUsingTheTrueAnchorType()
    {
        // RootCode is carried unchanged through every recursive step via the CTE's own self-
        // reference (t.RootCode / t2.RootCode) - a real, common hierarchy-walk idiom. Resolving
        // it correctly REQUIRES using the true anchor's own RootCode column as the CTE's type;
        // the wrongly-chosen "anchor" (the second recursive member, still selecting from the
        // Tree self-reference) can't resolve t2.RootCode at all while Tree is still being
        // resolved, so it degrades to Unknown instead.
        var view = Build(
            "CREATE TABLE dbo.Categories (CategoryCode VARCHAR(20) NOT NULL, ParentCode VARCHAR(20) NULL);",
            """
            CREATE VIEW dbo.vw_Tree AS
            WITH Tree AS (
                SELECT CategoryCode, ParentCode, CategoryCode AS RootCode FROM dbo.Categories WHERE ParentCode IS NULL
                UNION ALL
                SELECT c.CategoryCode, c.ParentCode, t.RootCode FROM dbo.Categories c INNER JOIN Tree t ON c.ParentCode = t.CategoryCode
                UNION ALL
                SELECT c.CategoryCode, c.ParentCode, t2.RootCode FROM dbo.Categories c INNER JOIN Tree t2 ON c.ParentCode = t2.CategoryCode
            )
            SELECT CategoryCode, ParentCode, RootCode FROM Tree;
            """);

        var rootCode = view.FindColumn("RootCode")!;

        var declared = Assert.IsType<ColumnProvenance.Declared>(rootCode.Provenance);
        Assert.Equal(SqlTypeCategory.VarChar, declared.Type.Category);
    }
}
