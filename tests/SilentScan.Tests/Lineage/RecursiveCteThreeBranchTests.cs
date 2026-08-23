using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Lineage;

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
