using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class VectorLiteralConversionScannerTests
{
    private static IReadOnlyList<VectorLiteralConversionFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return VectorLiteralConversionScanner.Scan(result, catalog);
    }

    [Theory]
    [InlineData("[1.0, true, 3.0]", "boolean")]
    [InlineData("[1.0, \"a\", 3.0]", "string")]
    [InlineData("[1.0, null, 3.0]", "null")]
    [InlineData("[1.0, {}, 3.0]", "object")]
    public void CastToVector_WithNonNumericJsonElement_Fires(string json, string expectedKind)
    {
        var findings = Scan($"SELECT CAST('{json}' AS VECTOR(3));");

        var finding = Assert.Single(findings);
        Assert.Equal(VectorLiteralConversionFindingKind.NonNumericJsonElement, finding.Kind);
        Assert.Equal(expectedKind, finding.ElementKind);
    }

    [Fact]
    public void ConvertToVector_WithNonNumericJsonElement_Fires()
    {
        var findings = Scan("SELECT CONVERT(VECTOR(3), '[1.0, true, 3.0]');");

        var finding = Assert.Single(findings);
        Assert.Equal(VectorLiteralConversionFindingKind.NonNumericJsonElement, finding.Kind);
    }

    [Fact]
    public void CastToVector_WithAllNumericElements_DoesNotFire()
    {
        var findings = Scan("SELECT CAST('[1.0, 2.0, 3.0]' AS VECTOR(3));");

        Assert.Empty(findings);
    }

    [Fact]
    public void CastToVector_WithElementCountMismatch_Fires()
    {
        var findings = Scan("SELECT CAST('[1.0, 2.0]' AS VECTOR(3));");

        var finding = Assert.Single(findings);
        Assert.Equal(VectorLiteralConversionFindingKind.ElementCountMismatch, finding.Kind);
        Assert.Equal(2, finding.ActualElementCount);
        Assert.Equal(3, finding.DeclaredDimensions);
    }

    [Fact]
    public void CastToVector_WithMalformedJson_DoesNotFire()
    {
        var findings = Scan("SELECT CAST('not json' AS VECTOR(3));");

        Assert.Empty(findings);
    }

    [Fact]
    public void CastToVector_WithNestedArrayElement_Fires()
    {
        var findings = Scan("SELECT CAST('[1.0, [2.0], 3.0]' AS VECTOR(3));");

        var finding = Assert.Single(findings);
        Assert.Equal(VectorLiteralConversionFindingKind.NonNumericJsonElement, finding.Kind);
        Assert.Equal("Array", finding.ElementKind);
    }

    [Fact]
    public void DeclareVectorVariable_WithNonNumericJsonInitializer_Fires()
    {
        var findings = Scan("DECLARE @v VECTOR(3) = '[1.0, true, 3.0]';");

        var finding = Assert.Single(findings);
        Assert.Equal(VectorLiteralConversionFindingKind.NonNumericJsonElement, finding.Kind);
    }

    [Fact]
    public void DeclareVectorVariable_WithNumericJsonInitializer_DoesNotFire()
    {
        var findings = Scan("DECLARE @v VECTOR(3) = '[1.0, 2.0, 3.0]';");

        Assert.Empty(findings);
    }

    [Fact]
    public void SetVectorVariable_WithNonNumericJsonAssignment_Fires()
    {
        var findings = Scan("""
            DECLARE @v VECTOR(3);
            SET @v = '[1.0, true, 3.0]';
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(VectorLiteralConversionFindingKind.NonNumericJsonElement, finding.Kind);
    }

    [Fact]
    public void CastToNonVectorType_WithBooleanJsonElement_DoesNotFire()
    {
        var findings = Scan("SELECT CAST('[1.0, true, 3.0]' AS NVARCHAR(100));");

        Assert.Empty(findings);
    }
}
