using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class GroupByValidityScannerTests
{
    private static IReadOnlyList<GroupByValidityFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return GroupByValidityScanner.Scan(result);
    }

    [Fact]
    public void SelectList_BareColumnNotInGroupByOrAggregate_Fires()
    {
        var findings = Scan("SELECT Id, Category, SUM(Amount) FROM dbo.Sale GROUP BY Category;");

        var finding = Assert.Single(findings);
        Assert.Equal(GroupByValidityFindingKind.SelectList, finding.Kind);
    }

    [Fact]
    public void SelectList_ColumnInsideCaseElseBranch_Fires()
    {
        var findings = Scan("SELECT CASE WHEN Category = 'x' THEN 1 ELSE Id END FROM dbo.Sale GROUP BY Category;");

        Assert.Contains(findings, f => f.Kind == GroupByValidityFindingKind.SelectList);
    }

    [Fact]
    public void Having_BareColumnNotInGroupByOrAggregate_Fires()
    {
        var findings = Scan("SELECT Category, SUM(Amount) FROM dbo.Sale GROUP BY Category HAVING Id > 1;");

        var finding = Assert.Single(findings);
        Assert.Equal(GroupByValidityFindingKind.Having, finding.Kind);
    }

    [Fact]
    public void OrderBy_BareColumnNotInGroupByOrAggregate_Fires()
    {
        var findings = Scan("SELECT Category, SUM(Amount) FROM dbo.Sale GROUP BY Category ORDER BY Id;");

        var finding = Assert.Single(findings);
        Assert.Equal(GroupByValidityFindingKind.OrderBy, finding.Kind);
    }

    [Fact]
    public void OrderBy_ColumnMatchingGroupByColumn_NegativeControl_DoesNotFire()
    {
        var findings = Scan("SELECT Category, SUM(Amount) FROM dbo.Sale GROUP BY Category ORDER BY Category;");

        Assert.Empty(findings);
    }

    [Fact]
    public void SelectList_ColumnInsideAggregate_NegativeControl_DoesNotFire()
    {
        var findings = Scan("SELECT Category, SUM(Amount), COUNT(*) FROM dbo.Sale GROUP BY Category;");

        Assert.Empty(findings);
    }

    [Fact]
    public void SelectList_ColumnMatchingGroupByColumn_NegativeControl_DoesNotFire()
    {
        var findings = Scan("SELECT Category, Category + '!' FROM dbo.Sale GROUP BY Category;");

        Assert.Empty(findings);
    }

    [Fact]
    public void SelectList_ExpressionShapeMatchesGroupByExpression_NegativeControl_DoesNotFire()
    {
        var findings = Scan("SELECT Id + 1, SUM(Amount) FROM dbo.Sale GROUP BY Id + 1;");

        Assert.Empty(findings);
    }

    [Fact]
    public void SelectList_ExpressionShapeDoesNotMatchGroupByExpression_Fires()
    {
        var findings = Scan("SELECT Id + 2, SUM(Amount) FROM dbo.Sale GROUP BY Id + 1;");

        Assert.Single(findings);
    }

    [Fact]
    public void SelectList_GroupingByPrimaryKey_StillRequiresOtherColumnsGrouped_Fires()
    {
        var findings = Scan("SELECT Id, Category FROM dbo.Sale GROUP BY Id;");

        Assert.Contains(findings, f => f.Kind == GroupByValidityFindingKind.SelectList);
    }

    [Fact]
    public void NoGroupByClause_NegativeControl_DoesNotFire()
    {
        var findings = Scan("SELECT Id, Category FROM dbo.Sale;");

        Assert.Empty(findings);
    }

    [Fact]
    public void GroupByRollup_Declines()
    {
        var findings = Scan("SELECT Id, Category, SUM(Amount) FROM dbo.Sale GROUP BY ROLLUP(Category);");

        Assert.Empty(findings);
    }

    [Fact]
    public void GroupingSets_Declines()
    {
        var findings = Scan("SELECT Id, Category, SUM(Amount) FROM dbo.Sale GROUP BY GROUPING SETS (Category, ());");

        Assert.Empty(findings);
    }

    [Fact]
    public void SelectList_ColumnInsideSubquery_NegativeControl_DoesNotFire()
    {
        var findings = Scan("SELECT Category, SUM(Amount), (SELECT MAX(Id) FROM dbo.Other) FROM dbo.Sale GROUP BY Category;");

        Assert.Empty(findings);
    }
}
