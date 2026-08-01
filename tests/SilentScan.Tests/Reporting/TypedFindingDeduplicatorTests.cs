using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Reporting;

public sealed class TypedFindingDeduplicatorTests
{
    private static PredicateOperand.Column Column(string table, string name) =>
        new(table, name, new SqlType(SqlTypeCategory.VarChar, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS")), Indexed: true, Depth: 0, Provenance: null!);

    private static TypedPredicateFinding Finding(
        PredicateOperand.Column column, PredicateOperand other, string op = "=", int line = 1) =>
        new(Verdict.ScanForced, column, other, op, "test.sql", line, 1);

    [Fact]
    public void Dedupe_IdenticalTableColumnOperatorAndOtherType_CollapsesToOne()
    {
        var findings = new[]
        {
            Finding(Column("dbo.Documents", "CreatedByUser"), new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int)), line: 10),
            Finding(Column("dbo.Documents", "CreatedByUser"), new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int)), line: 90),
        };

        var deduped = TypedFindingDeduplicator.Dedupe(findings);

        var single = Assert.Single(deduped);
        Assert.Equal(10, single.Line);
    }

    [Fact]
    public void Dedupe_DifferentOtherOperandType_KeepsBothDistinct()
    {
        var findings = new[]
        {
            Finding(Column("dbo.T", "Col"), new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int))),
            Finding(Column("dbo.T", "Col"), new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar))),
        };

        Assert.Equal(2, TypedFindingDeduplicator.Dedupe(findings).Count);
    }

    [Fact]
    public void Dedupe_DifferentCollationOnStringFamilyOtherOperand_KeepsBothDistinct()
    {
        var findings = new[]
        {
            Finding(Column("dbo.T", "Col"), new PredicateOperand.Value(new SqlType(SqlTypeCategory.VarChar, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS")))),
            Finding(Column("dbo.T", "Col"), new PredicateOperand.Value(new SqlType(SqlTypeCategory.VarChar, Collation: new Collation("Latin1_General_CI_AS")))),
        };

        Assert.Equal(2, TypedFindingDeduplicator.Dedupe(findings).Count);
    }

    [Fact]
    public void Dedupe_DifferentTargetTable_KeepsBothDistinct()
    {
        var findings = new[]
        {
            Finding(Column("dbo.Documents", "CreatedByUser"), new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int))),
            Finding(Column("dbo.Discussion", "CreatedByUser"), new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int))),
        };

        Assert.Equal(2, TypedFindingDeduplicator.Dedupe(findings).Count);
    }

    [Fact]
    public void Dedupe_ColumnVsColumnOtherOperand_UsesOtherColumnIdentity()
    {
        var findings = new[]
        {
            Finding(Column("dbo.T1", "A"), Column("dbo.T2", "B")),
            Finding(Column("dbo.T1", "A"), Column("dbo.T2", "B"), line: 5),
            Finding(Column("dbo.T1", "A"), Column("dbo.T3", "B"), line: 9),
        };

        Assert.Equal(2, TypedFindingDeduplicator.Dedupe(findings).Count);
    }

    [Fact]
    public void Dedupe_UnresolvedOtherOperand_MergesIdenticalShapedUnknowns()
    {
        var findings = new[]
        {
            Finding(Column("dbo.T", "Col"), new PredicateOperand.Value(null)),
            Finding(Column("dbo.T", "Col"), new PredicateOperand.Value(null), line: 5),
        };

        Assert.Single(TypedFindingDeduplicator.Dedupe(findings));
    }

    [Fact]
    public void Dedupe_DifferentOperator_KeepsBothDistinct()
    {
        var findings = new[]
        {
            Finding(Column("dbo.T", "Col"), new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int)), op: "="),
            Finding(Column("dbo.T", "Col"), new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int)), op: ">"),
        };

        Assert.Equal(2, TypedFindingDeduplicator.Dedupe(findings).Count);
    }
}
