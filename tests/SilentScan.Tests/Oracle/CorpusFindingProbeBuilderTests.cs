using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Oracle;

public sealed class CorpusFindingProbeBuilderTests
{
    private static readonly ColumnProvenance.BaseColumn Provenance =
        new("dbo.T", "Col", new SqlType(SqlTypeCategory.VarChar, Length: 10), Depth: 0);

    [Fact]
    public void Build_ColumnVsValue_RendersDeclareAndComparison()
    {
        var column = new PredicateOperand.Column("dbo.CodeFrequency", "Code", new SqlType(SqlTypeCategory.Char, Length: 1), Indexed: true, Depth: 0, Provenance);
        var other = new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int));
        var finding = new TypedPredicateFinding(Verdict.ScanForced, column, other, "<>", "file.sql", 1, 1);

        var probe = CorpusFindingProbeBuilder.Build(finding);

        Assert.Equal("""
            DECLARE @p INT;
            SELECT 1 FROM [dbo].[CodeFrequency] WHERE [Code] <> @p;
            """, probe);
    }

    [Fact]
    public void Build_ColumnVsColumn_RendersJoinComparison()
    {
        var column = new PredicateOperand.Column("dbo.FAQs", "CreatedByUser", new SqlType(SqlTypeCategory.NVarChar, Length: 100), Indexed: false, Depth: 0, Provenance);
        var other = new PredicateOperand.Column("dbo.Users", "UserID", new SqlType(SqlTypeCategory.Int), Indexed: true, Depth: 0, Provenance);
        var finding = new TypedPredicateFinding(Verdict.ScanForced, column, other, "=", "file.sql", 1, 1);

        var probe = CorpusFindingProbeBuilder.Build(finding);

        Assert.Equal("SELECT 1 FROM [dbo].[FAQs] AS t1 CROSS JOIN [dbo].[Users] AS t2 WHERE t1.[CreatedByUser] = t2.[UserID];", probe);
    }

    [Fact]
    public void Build_UnqualifiedTableName_BracketsWithoutSchema()
    {
        var column = new PredicateOperand.Column("T", "Col", new SqlType(SqlTypeCategory.Int), Indexed: false, Depth: 0, Provenance);
        var other = new PredicateOperand.Value(new SqlType(SqlTypeCategory.BigInt));
        var finding = new TypedPredicateFinding(Verdict.SeekPreserved, column, other, "=", "file.sql", 1, 1);

        var probe = CorpusFindingProbeBuilder.Build(finding);

        Assert.Contains("FROM [T] WHERE", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_OtherOperandTypeIsNull_ReturnsNull()
    {
        var column = new PredicateOperand.Column("dbo.T", "Col", new SqlType(SqlTypeCategory.Int), Indexed: false, Depth: 0, Provenance);
        var other = new PredicateOperand.Value(Type: null);
        var finding = new TypedPredicateFinding(Verdict.Unknown, column, other, "=", "file.sql", 1, 1);

        Assert.Null(CorpusFindingProbeBuilder.Build(finding));
    }

    [Fact]
    public void Build_OtherOperandIsUserDefinedType_ReturnsNull()
    {
        var column = new PredicateOperand.Column("dbo.T", "Col", new SqlType(SqlTypeCategory.Int), Indexed: false, Depth: 0, Provenance);
        var other = new PredicateOperand.Value(new SqlType(SqlTypeCategory.UserDefined));
        var finding = new TypedPredicateFinding(Verdict.Unknown, column, other, "=", "file.sql", 1, 1);

        Assert.Null(CorpusFindingProbeBuilder.Build(finding));
    }
}
