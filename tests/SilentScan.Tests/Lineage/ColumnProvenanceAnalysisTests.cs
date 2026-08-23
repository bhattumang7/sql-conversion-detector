using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Lineage;

public sealed class ColumnProvenanceAnalysisTests
{
    private static readonly ColumnProvenance.BaseColumn Base =
        new("dbo.Orders", "CustomerId", new SqlType(SqlTypeCategory.Int));

    [Fact]
    public void IsExpressionDerived_BaseColumn_False()
    {
        Assert.False(ColumnProvenanceAnalysis.IsExpressionDerived(Base));
    }

    [Fact]
    public void IsExpressionDerived_Cast_True()
    {
        var cast = new ColumnProvenance.Cast(new SqlType(SqlTypeCategory.VarChar, Length: 20), Base);

        Assert.True(ColumnProvenanceAnalysis.IsExpressionDerived(cast));
    }

    [Fact]
    public void IsExpressionDerived_Expression_True()
    {
        var expression = new ColumnProvenance.Expression(InferredType: null, Inputs: [Base]);

        Assert.True(ColumnProvenanceAnalysis.IsExpressionDerived(expression));
    }

    [Fact]
    public void IsExpressionDerived_Unknown_False()
    {

        Assert.False(ColumnProvenanceAnalysis.IsExpressionDerived(new ColumnProvenance.Unknown("reason")));
    }

    [Fact]
    public void IsExpressionDerived_UnionWithOneExpressionDerivedBranch_True()
    {

        var other = new ColumnProvenance.BaseColumn("dbo.Orders", "CustomerId", new SqlType(SqlTypeCategory.VarChar, Length: 20));
        var cast = new ColumnProvenance.Cast(new SqlType(SqlTypeCategory.VarChar, Length: 20), Base);
        var union = new ColumnProvenance.Union([other, cast]);

        Assert.True(ColumnProvenanceAnalysis.IsExpressionDerived(union));
    }

    [Fact]
    public void IsExpressionDerived_UnionWithAllBaseColumnBranches_False()
    {
        var other = new ColumnProvenance.BaseColumn("dbo.Orders", "CustomerId", new SqlType(SqlTypeCategory.Int));
        var union = new ColumnProvenance.Union([Base, other]);

        Assert.False(ColumnProvenanceAnalysis.IsExpressionDerived(union));
    }

    [Fact]
    public void IsExpressionDerived_NestedUnionWithExpressionDerivedBranch_True()
    {

        var other = new ColumnProvenance.BaseColumn("dbo.Orders", "CustomerId", new SqlType(SqlTypeCategory.Int));
        var cast = new ColumnProvenance.Cast(new SqlType(SqlTypeCategory.VarChar, Length: 20), Base);
        var nested = new ColumnProvenance.Union([new ColumnProvenance.Union([Base, other]), cast]);

        Assert.True(ColumnProvenanceAnalysis.IsExpressionDerived(nested));
    }

    [Fact]
    public void FindUnderlyingBaseColumns_DirectBaseColumn_ReturnsItself()
    {
        var found = ColumnProvenanceAnalysis.FindUnderlyingBaseColumns(Base);

        Assert.Equal([Base], found);
    }

    [Fact]
    public void FindUnderlyingBaseColumns_CastOfCastOfBaseColumn_ReturnsTheBaseColumn()
    {

        var innerCast = new ColumnProvenance.Cast(new SqlType(SqlTypeCategory.VarChar, Length: 20), Base);
        var outerCast = new ColumnProvenance.Cast(new SqlType(SqlTypeCategory.Int), innerCast);

        var found = ColumnProvenanceAnalysis.FindUnderlyingBaseColumns(outerCast);

        Assert.Equal([Base], found);
    }

    [Fact]
    public void FindUnderlyingBaseColumns_ExpressionCombiningTwoColumns_ReturnsBoth()
    {
        var other = new ColumnProvenance.BaseColumn("dbo.Orders", "Quantity", new SqlType(SqlTypeCategory.Int));
        var expression = new ColumnProvenance.Expression(InferredType: null, Inputs: [Base, other]);

        var found = ColumnProvenanceAnalysis.FindUnderlyingBaseColumns(expression);

        Assert.Equal([Base, other], found);
    }

    [Fact]
    public void FindUnderlyingBaseColumns_OpaqueLiteralExpression_ReturnsEmpty()
    {
        var literal = new ColumnProvenance.Expression(new SqlType(SqlTypeCategory.Int), Inputs: []);

        Assert.Empty(ColumnProvenanceAnalysis.FindUnderlyingBaseColumns(literal));
    }

    [Fact]
    public void FindUnderlyingBaseColumns_UnionOfBaseColumnAndCast_ReturnsBoth()
    {
        var other = new ColumnProvenance.BaseColumn("dbo.Orders", "CustomerId", new SqlType(SqlTypeCategory.VarChar, Length: 20));
        var cast = new ColumnProvenance.Cast(new SqlType(SqlTypeCategory.VarChar, Length: 20), Base);
        var union = new ColumnProvenance.Union([other, cast]);

        var found = ColumnProvenanceAnalysis.FindUnderlyingBaseColumns(union);

        Assert.Equal([other, Base], found);
    }

    [Fact]
    public void DescribeTransformationChain_StackedCasts_ListsOutermostFirst()
    {
        var innerCast = new ColumnProvenance.Cast(new SqlType(SqlTypeCategory.VarChar, Length: 20), Base, "vw_inner.sql", 5);
        var outerCast = new ColumnProvenance.Cast(new SqlType(SqlTypeCategory.Int), innerCast, "vw_outer.sql", 9);

        var chain = ColumnProvenanceAnalysis.DescribeTransformationChain(outerCast);

        Assert.Equal(2, chain.Count);
        Assert.Equal("vw_outer.sql", chain[0].SourcePath);
        Assert.Equal(9, chain[0].Line);
        Assert.Equal("vw_inner.sql", chain[1].SourcePath);
        Assert.Equal(5, chain[1].Line);
    }

    [Fact]
    public void DescribeTransformationChain_PlainBaseColumn_Empty()
    {
        Assert.Empty(ColumnProvenanceAnalysis.DescribeTransformationChain(Base));
    }

    [Fact]
    public void DescribeTransformationChain_UnionWithOneCastBranch_ListsOnlyTheCastBranchSite()
    {
        var passthroughBranch = new ColumnProvenance.BaseColumn("dbo.Orders", "CustomerId", new SqlType(SqlTypeCategory.VarChar, Length: 20));
        var castBranch = new ColumnProvenance.Cast(new SqlType(SqlTypeCategory.VarChar, Length: 20), Base, "vw_branch.sql", 12);
        var union = new ColumnProvenance.Union([passthroughBranch, castBranch]);

        var chain = ColumnProvenanceAnalysis.DescribeTransformationChain(union);

        var site = Assert.Single(chain);
        Assert.Equal("vw_branch.sql", site.SourcePath);
        Assert.Equal(12, site.Line);
    }

    [Fact]
    public void TryGetScalarType_UnionOfDifferingLengthStringBranches_WidensToTheWider()
    {

        var narrow = new ColumnProvenance.BaseColumn("dbo.A", "Col", new SqlType(SqlTypeCategory.VarChar, Length: 10));
        var wide = new ColumnProvenance.BaseColumn("dbo.B", "Col", new SqlType(SqlTypeCategory.VarChar, Length: 200));
        var union = new ColumnProvenance.Union([narrow, wide]);

        var type = ColumnProvenanceAnalysis.TryGetScalarType(union);

        Assert.Equal(200, type!.Length);
    }

    [Fact]
    public void TryGetScalarType_UnionOfDifferingLengthStringBranches_ReverseOrder_StillWidensToTheWider()
    {
        var wide = new ColumnProvenance.BaseColumn("dbo.A", "Col", new SqlType(SqlTypeCategory.VarChar, Length: 200));
        var narrow = new ColumnProvenance.BaseColumn("dbo.B", "Col", new SqlType(SqlTypeCategory.VarChar, Length: 10));
        var union = new ColumnProvenance.Union([wide, narrow]);

        var type = ColumnProvenanceAnalysis.TryGetScalarType(union);

        Assert.Equal(200, type!.Length);
    }

    [Fact]
    public void TryGetScalarType_UnionWithOneMaxBranch_ResultIsMax()
    {
        var bounded = new ColumnProvenance.BaseColumn("dbo.A", "Col", new SqlType(SqlTypeCategory.NVarChar, Length: 3750));
        var max = new ColumnProvenance.BaseColumn("dbo.B", "Col", new SqlType(SqlTypeCategory.NVarChar, IsMax: true));
        var union = new ColumnProvenance.Union([bounded, max]);

        var type = ColumnProvenanceAnalysis.TryGetScalarType(union);

        Assert.True(type!.IsMax);
    }

    [Fact]
    public void TryGetScalarType_UnionOfDecimalBranches_WidensPrecisionAndScale()
    {

        var a = new ColumnProvenance.BaseColumn("dbo.A", "Col", new SqlType(SqlTypeCategory.Decimal, Precision: 5, Scale: 3));
        var b = new ColumnProvenance.BaseColumn("dbo.B", "Col", new SqlType(SqlTypeCategory.Decimal, Precision: 10, Scale: 1));
        var union = new ColumnProvenance.Union([a, b]);

        var type = ColumnProvenanceAnalysis.TryGetScalarType(union);

        Assert.Equal(12, type!.Precision);
        Assert.Equal(3, type.Scale);
    }

    [Fact]
    public void TryGetScalarType_UnionOfSameLengthStringBranches_RegressionGuard_PreservesTheLength()
    {
        var a = new ColumnProvenance.BaseColumn("dbo.A", "Col", new SqlType(SqlTypeCategory.VarChar, Length: 20));
        var b = new ColumnProvenance.BaseColumn("dbo.B", "Col", new SqlType(SqlTypeCategory.VarChar, Length: 20));
        var union = new ColumnProvenance.Union([a, b]);

        Assert.Equal(20, ColumnProvenanceAnalysis.TryGetScalarType(union)!.Length);
    }
}
