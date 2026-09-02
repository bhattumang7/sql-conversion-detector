using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class VectorFunctionArgumentScannerTests
{
    private static IReadOnlyList<VectorFunctionArgumentFinding> Scan(string sql, string extraDdl = "")
    {
        var ddl = "CREATE TABLE dbo.Embedding (Id INT NOT NULL PRIMARY KEY, RawVector VARCHAR(4000) NOT NULL, Query VECTOR(3) NOT NULL, OtherQuery VECTOR(4) NOT NULL);"
            + (extraDdl.Length > 0 ? $"\nGO\n{extraDdl}" : string.Empty);
        var result = SqlScriptParser.ParseText("test.sql", $"{ddl}\nGO\n{sql}");
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return VectorFunctionArgumentScanner.Scan(result, catalog);
    }

    [Fact]
    public void VectorDistance_WithVarcharColumnOperand_Fires()
    {
        var findings = Scan("SELECT VECTOR_DISTANCE('cosine', RawVector, Query) FROM dbo.Embedding;");

        var finding = Assert.Single(findings);
        Assert.Equal(VectorFunctionArgumentFindingKind.NonVectorOperand, finding.Kind);
        Assert.Equal("VECTOR_DISTANCE", finding.FunctionName);
        Assert.Equal("first vector argument", finding.ArgumentDescription);
    }

    [Fact]
    public void VectorDistance_WithStringLiteralOperand_Fires()
    {
        var findings = Scan("SELECT VECTOR_DISTANCE('cosine', '[1,2,3]', Query) FROM dbo.Embedding;");

        var finding = Assert.Single(findings);
        Assert.Equal(VectorFunctionArgumentFindingKind.NonVectorOperand, finding.Kind);
    }

    [Fact]
    public void VectorDistance_WithTwoMatchingVectorColumns_DoesNotFire()
    {
        var ddl = "CREATE TABLE dbo.Pair (A VECTOR(3) NOT NULL, B VECTOR(3) NOT NULL);";
        var findings = Scan("SELECT VECTOR_DISTANCE('cosine', A, B) FROM dbo.Pair;", ddl);

        Assert.Empty(findings);
    }

    [Fact]
    public void VectorDistance_WithMismatchedVectorColumnDimensions_Fires()
    {
        var findings = Scan("SELECT VECTOR_DISTANCE('cosine', Query, OtherQuery) FROM dbo.Embedding;");

        var finding = Assert.Single(findings);
        Assert.Equal(VectorFunctionArgumentFindingKind.DimensionMismatch, finding.Kind);
    }

    [Fact]
    public void VectorDistance_WithCastToMatchingVector_DoesNotFire()
    {
        var findings = Scan("SELECT VECTOR_DISTANCE('cosine', CAST(RawVector AS VECTOR(3)), Query) FROM dbo.Embedding;");

        Assert.Empty(findings);
    }

    [Fact]
    public void VectorDistance_WithNullLiteralOperand_DoesNotFire()
    {
        var findings = Scan("SELECT VECTOR_DISTANCE('cosine', NULL, Query) FROM dbo.Embedding;");

        Assert.Empty(findings);
    }

    [Fact]
    public void VectorDistance_WithUnresolvableExpressionOperand_DoesNotFire()
    {
        var findings = Scan("SELECT VECTOR_DISTANCE('cosine', SomeUdf(Id), Query) FROM dbo.Embedding;");

        Assert.Empty(findings);
    }

    [Fact]
    public void VectorNorm_WithVarcharOperand_Fires()
    {
        var findings = Scan("SELECT VECTOR_NORM(RawVector, 'norm2') FROM dbo.Embedding;");

        var finding = Assert.Single(findings);
        Assert.Equal("VECTOR_NORM", finding.FunctionName);
        Assert.Equal(VectorFunctionArgumentFindingKind.NonVectorOperand, finding.Kind);
    }

    [Fact]
    public void VectorNorm_WithVectorColumnOperand_DoesNotFire()
    {
        var findings = Scan("SELECT VECTOR_NORM(Query, 'norm2') FROM dbo.Embedding;");

        Assert.Empty(findings);
    }

    [Fact]
    public void VectorProperty_WithLocalVariableVarcharOperand_Fires()
    {
        var findings = Scan("DECLARE @v VARCHAR(50) = '[1,2,3]'; SELECT VECTORPROPERTY(@v, 'Dimensions');");

        var finding = Assert.Single(findings);
        Assert.Equal("VECTORPROPERTY", finding.FunctionName);
    }

    [Fact]
    public void VectorProperty_WithLocalVariableVectorOperand_DoesNotFire()
    {
        var findings = Scan("DECLARE @v VECTOR(3) = CAST('[1,2,3]' AS VECTOR(3)); SELECT VECTORPROPERTY(@v, 'Dimensions');");

        Assert.Empty(findings);
    }

    [Fact]
    public void ProcedureParameterTypedNonVector_Fires()
    {
        var ddl = "CREATE PROCEDURE dbo.P_Distance @a VARCHAR(50), @b VECTOR(3) AS BEGIN SELECT VECTOR_DISTANCE('cosine', @a, @b); END;";
        var findings = Scan("SELECT 1;", ddl);

        var finding = Assert.Single(findings);
        Assert.Equal(VectorFunctionArgumentFindingKind.NonVectorOperand, finding.Kind);
    }

    [Fact]
    public void UnrelatedFunctionCall_NeverFires()
    {
        var findings = Scan("SELECT UPPER(RawVector) FROM dbo.Embedding;");

        Assert.Empty(findings);
    }
}
