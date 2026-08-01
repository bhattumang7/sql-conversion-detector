using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;

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
        // Unresolvable provenance is its own honestly-reported UNKNOWN case, not this rule's concern.
        Assert.False(ColumnProvenanceAnalysis.IsExpressionDerived(new ColumnProvenance.Unknown("reason")));
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
        // Mirrors the int -> varchar -> int round trip: two stacked CASTs, one base column underneath.
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
}
