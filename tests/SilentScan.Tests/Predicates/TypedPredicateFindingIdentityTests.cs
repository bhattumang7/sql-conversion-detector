using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates;

public sealed class TypedPredicateFindingIdentityTests
{
    private static PredicateOperand.Column Column(string table, string column) =>
        new(table, column, new SqlType(SqlTypeCategory.VarChar), Indexed: true, Depth: 0, new ColumnProvenance.BaseColumn(table, column, new SqlType(SqlTypeCategory.VarChar), Depth: 0));

    [Fact]
    public void ComputeFingerprint_SameShapeDifferentSourceLocation_ProducesTheSameFingerprint()
    {
        var column = Column("dbo.Orders", "Code");
        var other = new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar));

        var first = TypedPredicateFindingIdentity.ComputeFingerprint(column, other, "=");
        var second = TypedPredicateFindingIdentity.ComputeFingerprint(column, other, "=");

        Assert.Equal(first, second);
    }

    [Fact]
    public void ComputeFingerprint_TableNameVsColumnNameBoundaryShift_DoesNotCollide()
    {

        var columnA = Column("dbo.A", "BC");
        var columnB = Column("dbo.AB", "C");
        var other = new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int));

        var fingerprintA = TypedPredicateFindingIdentity.ComputeFingerprint(columnA, other, "=");
        var fingerprintB = TypedPredicateFindingIdentity.ComputeFingerprint(columnB, other, "=");

        Assert.NotEqual(fingerprintA, fingerprintB);
    }

    [Fact]
    public void ComputeFingerprint_DifferentOperator_ProducesADifferentFingerprint()
    {
        var column = Column("dbo.Orders", "Code");
        var other = new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar));

        var equalsFingerprint = TypedPredicateFindingIdentity.ComputeFingerprint(column, other, "=");
        var likeFingerprint = TypedPredicateFindingIdentity.ComputeFingerprint(column, other, "LIKE");

        Assert.NotEqual(equalsFingerprint, likeFingerprint);
    }
}
