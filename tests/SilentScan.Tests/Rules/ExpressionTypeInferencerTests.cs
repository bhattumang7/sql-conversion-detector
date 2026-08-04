using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Rules;

/// <summary>
/// Direct unit tests against the shared expression engine, isolated from any pass-specific
/// column/scope resolution: <paramref name="resolveLeaf"/> is a hand-built stub mapping bare
/// identifier names to types, exercising exactly the recursive/combination logic this class
/// owns. Oracle-verified typing claims (CASE/COALESCE/IIF merge by precedence; NULLIF always
/// returns expr1's own type) are documented on the class itself - see its remarks.
/// </summary>
public sealed class ExpressionTypeInferencerTests
{
    private static readonly SqlType IntType = new(SqlTypeCategory.Int);
    private static readonly SqlType DecimalType = new(SqlTypeCategory.Decimal, Precision: 9, Scale: 2);
    private static readonly SqlType VarCharType = new(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"));
    private static readonly SqlType NVarCharType = new(SqlTypeCategory.NVarChar, Length: 20);

    private static ScalarExpression ParseExpression(string expressionSql)
    {
        var parser = new TSql160Parser(true);
        using var reader = new StringReader($"SELECT {expressionSql};");
        var fragment = parser.Parse(reader, out var errors);
        Assert.Empty(errors);

        var script = (TSqlScript)fragment;
        var select = (SelectStatement)script.Batches[0].Statements[0];
        var spec = (QuerySpecification)select.QueryExpression;
        return ((SelectScalarExpression)spec.SelectElements[0]).Expression;
    }

    /// <summary>Resolves a bare column reference like "IntCol" to a stub type by name - anything else falls through to null (this class's tests never need variables/functions).</summary>
    private static SqlType? StubLeaf(ScalarExpression expression, IReadOnlyDictionary<string, SqlType?> typesByName) =>
        expression is ColumnReferenceExpression { MultiPartIdentifier.Identifiers: [.., { } last] }
            ? typesByName.GetValueOrDefault(last.Value)
            : null;

    private static SqlType? Resolve(string expressionSql, IReadOnlyDictionary<string, SqlType?> typesByName) =>
        ExpressionTypeInferencer.Resolve(ParseExpression(expressionSql), e => StubLeaf(e, typesByName), typeAliases: null);

    [Fact]
    public void Resolve_Arithmetic_CombinesByPrecedence()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType, ["DecCol"] = DecimalType };

        var result = Resolve("IntCol * DecCol", typesByName);

        Assert.Equal(SqlTypeCategory.Decimal, result!.Category);
    }

    [Fact]
    public void Resolve_Parenthesis_UnwrapsToInnerType()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType };

        Assert.Equal(SqlTypeCategory.Int, Resolve("(IntCol)", typesByName)!.Category);
    }

    [Fact]
    public void Resolve_Unary_UnwrapsToInnerType()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType };

        Assert.Equal(SqlTypeCategory.Int, Resolve("-IntCol", typesByName)!.Category);
    }

    [Fact]
    public void Resolve_CastCall_ResolvesTargetType()
    {
        Assert.Equal(SqlTypeCategory.NVarChar, Resolve("CAST(1 AS NVARCHAR(10))", new Dictionary<string, SqlType?>())!.Category);
    }

    [Fact]
    public void Resolve_SearchedCase_OracleVerified_MergesBranchesByPrecedence()
    {
        // Oracle-verified: CASE WHEN 1=1 THEN IntCol ELSE DecCol END resolves DECIMAL against
        // the real server, not INT - the branches merge by T-SQL data type precedence exactly
        // like a binary operator, not "whichever branch executes."
        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType, ["DecCol"] = DecimalType };

        var result = Resolve("CASE WHEN 1 = 1 THEN IntCol ELSE DecCol END", typesByName);

        Assert.Equal(SqlTypeCategory.Decimal, result!.Category);
    }

    [Fact]
    public void Resolve_SimpleCase_OracleVerified_MergesBranchesByPrecedence()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType, ["DecCol"] = DecimalType };

        var result = Resolve("CASE IntCol WHEN 1 THEN IntCol ELSE DecCol END", typesByName);

        Assert.Equal(SqlTypeCategory.Decimal, result!.Category);
    }

    [Fact]
    public void Resolve_Coalesce_OracleVerified_MergesAllBranchesByPrecedence()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["VarcharCol"] = VarCharType, ["NVarcharCol"] = NVarCharType };

        var result = Resolve("COALESCE(VarcharCol, NVarcharCol)", typesByName);

        // Oracle-verified: COALESCE(varcharCol, nvarcharCol) resolves NVARCHAR - nvarchar wins
        // T-SQL precedence over varchar.
        Assert.Equal(SqlTypeCategory.NVarChar, result!.Category);
    }

    [Fact]
    public void Resolve_Coalesce_OneUnresolvableBranch_NullsWholeResult()
    {
        // A branch this pass can't type might be the actual precedence winner - never guess
        // from only the branches it COULD type.
        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType };

        Assert.Null(Resolve("COALESCE(IntCol, UnknownCol)", typesByName));
    }

    [Fact]
    public void Resolve_IIf_OracleVerified_BehavesLikeCase()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType, ["DecCol"] = DecimalType };

        var result = Resolve("IIF(1 = 1, IntCol, DecCol)", typesByName);

        Assert.Equal(SqlTypeCategory.Decimal, result!.Category);
    }

    [Fact]
    public void Resolve_NullIf_OracleVerified_AlwaysReturnsFirstExpressionType_NotPrecedenceMerge()
    {
        // Oracle-verified: NULLIF(intCol, decCol) resolves INT, NOT the DECIMAL a CASE/COALESCE/
        // IIF merge of the same two types would produce - NULLIF is documented, and confirmed
        // against the real server, to always return expr1's own type regardless of expr2.
        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType, ["DecCol"] = DecimalType };

        var result = Resolve("NULLIF(IntCol, DecCol)", typesByName);

        Assert.Equal(SqlTypeCategory.Int, result!.Category);
    }

    [Fact]
    public void Resolve_NullIf_ReversedOperandOrder_StillReturnsFirstExpressionType()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType, ["DecCol"] = DecimalType };

        var result = Resolve("NULLIF(DecCol, IntCol)", typesByName);

        Assert.Equal(SqlTypeCategory.Decimal, result!.Category);
    }

    [Fact]
    public void Resolve_SearchedCase_OracleVerified_BareNullBranchIsIgnoredNotMergeed()
    {
        // Oracle-verified: CASE WHEN 1=1 THEN NULL ELSE IntCol END resolves INT against the real
        // server - an untyped NULL branch has no type of its own to merge into the precedence
        // winner, so it must be ignored entirely rather than nulling the whole result the way an
        // actually-unresolvable branch (a column this pass can't type) still correctly does.
        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType };

        var result = Resolve("CASE WHEN 1 = 1 THEN NULL ELSE IntCol END", typesByName);

        Assert.Equal(SqlTypeCategory.Int, result!.Category);
    }

    [Fact]
    public void Resolve_Coalesce_BareNullArgument_IsIgnoredNotMerged()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType };

        var result = Resolve("COALESCE(NULL, IntCol)", typesByName);

        Assert.Equal(SqlTypeCategory.Int, result!.Category);
    }

    [Fact]
    public void Resolve_IIf_BareNullBranch_IsIgnoredNotMerged()
    {
        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType };

        var result = Resolve("IIF(1 = 1, NULL, IntCol)", typesByName);

        Assert.Equal(SqlTypeCategory.Int, result!.Category);
    }

    [Fact]
    public void Resolve_Coalesce_OneUnresolvableNonNullBranch_StillNullsWholeResult()
    {
        // Distinguishes "bare NULL literal, safely ignorable" from "a real branch this pass
        // just couldn't type" (UnknownCol) - the latter must still null the whole result,
        // exactly as Resolve_Coalesce_OneUnresolvableBranch_NullsWholeResult already covers for
        // two real columns. Re-asserted here alongside the NULL-handling change to prove the
        // NULL special-case didn't accidentally widen into "ignore anything unresolvable."
        var typesByName = new Dictionary<string, SqlType?> { ["IntCol"] = IntType };

        Assert.Null(Resolve("COALESCE(NULL, IntCol, UnknownCol)", typesByName));
    }

    [Fact]
    public void Resolve_SearchedCase_OracleVerified_SameCategoryDifferingLength_WidensToTheLonger()
    {
        // Oracle-verified (sys.dm_exec_describe_first_result_set): CASE WHEN 1=1 THEN
        // Nvarchar10Col ELSE Nvarchar20Col END resolves nvarchar(20) against the real server -
        // the WIDER of the two same-category branches, never just whichever branch this pass
        // happened to resolve first (the real bug this test guards: DNN Platform's
        // vw_Profile.PropertyValue - CASE WHEN PropertyText IS NULL THEN PropertyValue ELSE
        // PropertyText END - mixed nvarchar(3750) with nvarchar(MAX) and was inferred as
        // nvarchar(3750), a genuine mismatch against the real deployed column).
        var typesByName = new Dictionary<string, SqlType?>
        {
            ["Nvarchar10Col"] = new SqlType(SqlTypeCategory.NVarChar, Length: 10),
            ["Nvarchar20Col"] = new SqlType(SqlTypeCategory.NVarChar, Length: 20),
        };

        var result = Resolve("CASE WHEN 1 = 1 THEN Nvarchar10Col ELSE Nvarchar20Col END", typesByName);

        Assert.Equal(SqlTypeCategory.NVarChar, result!.Category);
        Assert.Equal(20, result.Length);
    }

    [Fact]
    public void Resolve_SearchedCase_OracleVerified_SameCategoryDifferingLengthReversedOrder_StillWidensToTheLonger()
    {
        var typesByName = new Dictionary<string, SqlType?>
        {
            ["Nvarchar10Col"] = new SqlType(SqlTypeCategory.NVarChar, Length: 10),
            ["Nvarchar20Col"] = new SqlType(SqlTypeCategory.NVarChar, Length: 20),
        };

        var result = Resolve("CASE WHEN 1 = 1 THEN Nvarchar20Col ELSE Nvarchar10Col END", typesByName);

        Assert.Equal(20, result!.Length);
    }

    [Fact]
    public void Resolve_SearchedCase_OracleVerified_OneBranchIsMax_ResultIsMaxRegardlessOfPosition()
    {
        // Oracle-verified: whichever side is MAX, the CASE result is nvarchar(max) - never the
        // OTHER (fixed-length) branch's own length.
        var typesByName = new Dictionary<string, SqlType?>
        {
            ["Nvarchar10Col"] = new SqlType(SqlTypeCategory.NVarChar, Length: 10),
            ["NvarcharMaxCol"] = new SqlType(SqlTypeCategory.NVarChar, IsMax: true),
        };

        var thenIsMax = Resolve("CASE WHEN 1 = 1 THEN NvarcharMaxCol ELSE Nvarchar10Col END", typesByName);
        var elseIsMax = Resolve("CASE WHEN 1 = 1 THEN Nvarchar10Col ELSE NvarcharMaxCol END", typesByName);

        Assert.True(thenIsMax!.IsMax);
        Assert.True(elseIsMax!.IsMax);
    }

    [Fact]
    public void Resolve_SearchedCase_OracleVerified_SameCategorySameLength_PreservesTheLength()
    {
        // Regression guard: the widening fix must not perturb the (dominant, already-correct)
        // same-length case.
        var typesByName = new Dictionary<string, SqlType?>
        {
            ["A"] = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS")),
            ["B"] = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS")),
        };

        var result = Resolve("CASE WHEN 1 = 1 THEN A ELSE B END", typesByName);

        Assert.Equal(20, result!.Length);
    }
}
