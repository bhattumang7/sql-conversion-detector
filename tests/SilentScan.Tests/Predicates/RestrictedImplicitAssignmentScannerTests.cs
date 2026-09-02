using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class RestrictedImplicitAssignmentScannerTests
{
    private static IReadOnlyList<RestrictedImplicitAssignmentFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return RestrictedImplicitAssignmentScanner.Scan(result, catalog);
    }

    [Fact]
    public void VariantVariableAssignedToXmlVariable_Fires()
    {
        var findings = Scan("""
            DECLARE @v sql_variant = 5;
            DECLARE @x xml;
            SET @x = @v;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("@x", finding.TargetVariableName);
        Assert.Equal("@v", finding.SourceVariableName);
    }

    [Fact]
    public void XmlVariableAssignedToVariantVariable_Fires()
    {
        var findings = Scan("""
            DECLARE @x xml = '<a/>';
            DECLARE @v sql_variant;
            SET @v = @x;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("@v", finding.TargetVariableName);
        Assert.Equal("@x", finding.SourceVariableName);
    }

    [Fact]
    public void VariantVariableAssignedToIntVariable_Fires()
    {
        var findings = Scan("""
            DECLARE @v sql_variant = 5;
            DECLARE @i int;
            SET @i = @v;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("@i", finding.TargetVariableName);
        Assert.Equal("@v", finding.SourceVariableName);
    }

    [Fact]
    public void XmlVariableAssignedToIntVariable_Fires()
    {
        var findings = Scan("""
            DECLARE @x xml = '<a/>';
            DECLARE @i int;
            SET @i = @x;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("@i", finding.TargetVariableName);
        Assert.Equal("@x", finding.SourceVariableName);
    }

    [Fact]
    public void IntVariableAssignedToXmlVariable_Fires()
    {
        var findings = Scan("""
            DECLARE @i int = 5;
            DECLARE @x xml;
            SET @x = @i;
            """);

        Assert.Single(findings);
    }

    [Fact]
    public void ProcedureParameterTypedXml_AssignedFromVariantLocal_Fires()
    {
        var findings = Scan("""
            CREATE PROCEDURE dbo.TestProc (@x xml)
            AS
            BEGIN
                DECLARE @v sql_variant = 5;
                SET @x = @v;
            END
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("@x", finding.TargetVariableName);
        Assert.Equal("@v", finding.SourceVariableName);
    }

    [Fact]
    public void BothVariablesXml_NeverFires()
    {
        var findings = Scan("""
            DECLARE @a xml = '<a/>';
            DECLARE @b xml;
            SET @b = @a;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void BothVariablesSqlVariant_NeverFires()
    {
        var findings = Scan("""
            DECLARE @a sql_variant = 5;
            DECLARE @b sql_variant;
            SET @b = @a;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void IntVariableAssignedToVariantVariable_NeverFires()
    {
        var findings = Scan("""
            DECLARE @i int = 5;
            DECLARE @v sql_variant;
            SET @v = @i;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void VarcharVariableAssignedToXmlVariable_NeverFires()
    {
        var findings = Scan("""
            DECLARE @s varchar(50) = '<a/>';
            DECLARE @x xml;
            SET @x = @s;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void VarbinaryVariableAssignedToXmlVariable_NeverFires()
    {
        var findings = Scan("""
            DECLARE @b varbinary(50) = 0x3C612F3E;
            DECLARE @x xml;
            SET @x = @b;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void VariantVariableAssignedLiteral_NeverFires()
    {
        var findings = Scan("""
            DECLARE @v sql_variant;
            SET @v = 5;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void SecondBatch_LocalScopeDoesNotLeakAcrossBatches()
    {
        var findings = Scan("""
            DECLARE @v sql_variant = 5;
            GO
            DECLARE @x xml;
            SET @x = @v;
            """);

        Assert.Empty(findings);
    }
}
