using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class XmlSchemaCollectionMismatchScannerTests
{
    private static IReadOnlyList<XmlSchemaCollectionMismatchFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return XmlSchemaCollectionMismatchScanner.Scan(result, catalog);
    }

    [Fact]
    public void SetAssignment_BetweenDifferentSchemaCollections_Fires()
    {
        var findings = Scan("""
            DECLARE @order XML(dbo.OrderSchema) = '<Order/>';
            DECLARE @invoice XML(dbo.InvoiceSchema);
            SET @invoice = @order;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("@invoice", finding.TargetVariableName);
        Assert.Equal("dbo.InvoiceSchema", finding.TargetSchemaCollectionName);
        Assert.Equal("@order", finding.SourceVariableName);
        Assert.Equal("dbo.OrderSchema", finding.SourceSchemaCollectionName);
    }

    [Fact]
    public void DeclareInitializer_BetweenDifferentSchemaCollections_Fires()
    {
        var findings = Scan("""
            DECLARE @order XML(dbo.OrderSchema) = '<Order/>';
            DECLARE @invoice XML(dbo.InvoiceSchema) = @order;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("@invoice", finding.TargetVariableName);
    }

    [Fact]
    public void SetAssignment_BetweenSameSchemaCollection_DoesNotFire()
    {
        var findings = Scan("""
            DECLARE @a XML(dbo.OrderSchema) = '<Order/>';
            DECLARE @b XML(dbo.OrderSchema);
            SET @b = @a;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void SetAssignment_ThroughConvert_DoesNotFire()
    {
        var findings = Scan("""
            DECLARE @order XML(dbo.OrderSchema) = '<Order/>';
            DECLARE @invoice XML(dbo.InvoiceSchema);
            SET @invoice = CONVERT(XML(dbo.InvoiceSchema), @order);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void SetAssignment_FromUntypedXmlVariable_DoesNotFire()
    {
        var findings = Scan("""
            DECLARE @order XML;
            DECLARE @invoice XML(dbo.InvoiceSchema);
            SET @invoice = @order;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void SetAssignment_ToUntypedXmlVariable_DoesNotFire()
    {
        var findings = Scan("""
            DECLARE @order XML(dbo.OrderSchema) = '<Order/>';
            DECLARE @invoice XML;
            SET @invoice = @order;
            """);

        Assert.Empty(findings);
    }
}
