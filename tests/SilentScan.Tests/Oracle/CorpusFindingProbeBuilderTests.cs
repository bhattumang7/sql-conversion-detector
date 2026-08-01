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

    [Fact]
    public void Build_LiteralOperand_RendersTheLiteralInsteadOfADeclare()
    {
        // docs/audit-remediation-plan.md Phase 5.2, audit finding C2: a DECLARE @p probe can be
        // constant-folded differently than the original literal comparison, so a literal-
        // sourced operand reconstructs the literal text exactly rather than substituting a
        // same-typed variable.
        var column = new PredicateOperand.Column("dbo.Users", "DisplayName", new SqlType(SqlTypeCategory.VarChar, Length: 40), Indexed: true, Depth: 0, Provenance);
        var other = new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 5), IsLiteral: true, LiteralText: "N'Alice'");
        var finding = new TypedPredicateFinding(Verdict.ScanForced, column, other, "=", "file.sql", 1, 1);

        var probe = CorpusFindingProbeBuilder.Build(finding);

        Assert.Equal("SELECT 1 FROM [dbo].[Users] WHERE [DisplayName] = N'Alice';", probe);
    }

    [Fact]
    public void Build_LiteralOperandThatCouldNotBeRendered_FailsClosedInsteadOfSubstitutingAVariable()
    {
        var column = new PredicateOperand.Column("dbo.T", "Col", new SqlType(SqlTypeCategory.Int), Indexed: false, Depth: 0, Provenance);
        var other = new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int), IsLiteral: true, LiteralText: null);
        var finding = new TypedPredicateFinding(Verdict.Unknown, column, other, "=", "file.sql", 1, 1);

        Assert.Null(CorpusFindingProbeBuilder.Build(finding));
    }

    [Fact]
    public void Build_InOperator_NormalizesToEqualityForProbeSyntax()
    {
        // `Col IN (@p)` isn't valid SQL for a single scalar operand - the IN-list classifier
        // already collapsed the list to one effective type (docs/audit-remediation-plan.md
        // Phase 4.3), so an equality probe against that same type is a faithful stand-in.
        var column = new PredicateOperand.Column("dbo.T", "Col", new SqlType(SqlTypeCategory.VarChar, Length: 20), Indexed: false, Depth: 0, Provenance);
        var other = new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 20));
        var finding = new TypedPredicateFinding(Verdict.ScanForced, column, other, "IN", "file.sql", 1, 1);

        var probe = CorpusFindingProbeBuilder.Build(finding);

        Assert.Equal("""
            DECLARE @p NVARCHAR(20);
            SELECT 1 FROM [dbo].[T] WHERE [Col] = @p;
            """, probe);
    }

    [Fact]
    public void Build_ColumnHasImmediateRelation_QueriesTheViewNotTheBaseTable()
    {
        // A depth>=1 finding's TableQualifiedName/ColumnName always name the ultimate base
        // table (needed for the plan-matching signal), but the probe itself must query what the
        // source predicate actually referenced - the view - or it never exercises the view
        // layer the finding claims the conversion is inherited through at all.
        var column = new PredicateOperand.Column(
            "dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20), Indexed: true, Depth: 1, Provenance,
            ImmediateRelationQualifiedName: "dbo.vw_Orders", ImmediateColumnName: "Code");
        var other = new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 20));
        var finding = new TypedPredicateFinding(Verdict.ScanForced, column, other, "=", "file.sql", 1, 1);

        var probe = CorpusFindingProbeBuilder.Build(finding);

        Assert.Equal("""
            DECLARE @p NVARCHAR(20);
            SELECT 1 FROM [dbo].[vw_Orders] WHERE [Code] = @p;
            """, probe);
    }

    [Fact]
    public void Build_ColumnVsColumnBothHaveImmediateRelations_QueriesBothViews()
    {
        var column = new PredicateOperand.Column(
            "dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20), Indexed: true, Depth: 1, Provenance,
            ImmediateRelationQualifiedName: "dbo.vw_Orders", ImmediateColumnName: "Code");
        var other = new PredicateOperand.Column(
            "dbo.Users", "UserCode", new SqlType(SqlTypeCategory.NVarChar, Length: 20), Indexed: true, Depth: 1, Provenance,
            ImmediateRelationQualifiedName: "dbo.vw_Users", ImmediateColumnName: "Code");
        var finding = new TypedPredicateFinding(Verdict.ScanForced, column, other, "=", "file.sql", 1, 1);

        var probe = CorpusFindingProbeBuilder.Build(finding);

        Assert.Equal("SELECT 1 FROM [dbo].[vw_Orders] AS t1 CROSS JOIN [dbo].[vw_Users] AS t2 WHERE t1.[Code] = t2.[Code];", probe);
    }

    [Fact]
    public void Build_NoImmediateRelation_FallsBackToBaseTable()
    {
        // Depth 0 (a direct base-table predicate) never sets ImmediateRelation - falls back to
        // the same TableQualifiedName/ColumnName every existing test above already exercises.
        var column = new PredicateOperand.Column("dbo.T", "Col", new SqlType(SqlTypeCategory.VarChar, Length: 10), Indexed: true, Depth: 0, Provenance);
        var other = new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 10));
        var finding = new TypedPredicateFinding(Verdict.ScanForced, column, other, "=", "file.sql", 1, 1);

        var probe = CorpusFindingProbeBuilder.Build(finding);

        Assert.Contains("FROM [dbo].[T] WHERE [Col]", probe, StringComparison.Ordinal);
    }
}
