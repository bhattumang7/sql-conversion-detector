using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class XmlSchemaCollectionDisallowedTypeScannerTests
{
    private static IReadOnlyList<XmlSchemaCollectionDisallowedTypeFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return XmlSchemaCollectionDisallowedTypeScanner.Scan(result, catalog);
    }

    [Fact]
    public void NotationAsAttributeType_Fires()
    {
        var findings = Scan("""
            CREATE XML SCHEMA COLLECTION dbo.OrderSchema AS N'
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:complexType name="Order">
                <xs:attribute name="Format" type="xs:NOTATION"/>
              </xs:complexType>
            </xs:schema>';
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(XmlSchemaCollectionDisallowedTypeKind.NotationType, finding.Kind);
        Assert.Equal("dbo.OrderSchema", finding.SchemaCollectionQualifiedName);
    }

    [Fact]
    public void IdRefAsElementType_Fires()
    {
        var findings = Scan("""
            CREATE XML SCHEMA COLLECTION dbo.OrderSchema AS N'
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="CustomerRef" type="xs:IDREF"/>
            </xs:schema>';
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(XmlSchemaCollectionDisallowedTypeKind.IdOrIdRefType, finding.Kind);
        Assert.Equal("IDREF", finding.XsdTypeName);
    }

    [Fact]
    public void IdRefsAsElementType_Fires()
    {
        var findings = Scan("""
            CREATE XML SCHEMA COLLECTION dbo.OrderSchema AS N'
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="RefsRoot" type="xs:IDREFS"/>
            </xs:schema>';
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(XmlSchemaCollectionDisallowedTypeKind.IdOrIdRefType, finding.Kind);
        Assert.Equal("IDREFS", finding.XsdTypeName);
    }

    [Fact]
    public void IdAsExtensionBase_Fires()
    {
        var findings = Scan("""
            CREATE XML SCHEMA COLLECTION dbo.OrderSchema AS N'
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:complexType name="MyType">
                <xs:simpleContent>
                  <xs:extension base="xs:ID"/>
                </xs:simpleContent>
              </xs:complexType>
              <xs:element name="Root" type="MyType"/>
            </xs:schema>';
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(XmlSchemaCollectionDisallowedTypeKind.IdOrIdRefType, finding.Kind);
        Assert.Equal("ID", finding.XsdTypeName);
    }

    [Fact]
    public void IdRefAsRestrictionBase_Fires()
    {
        var findings = Scan("""
            CREATE XML SCHEMA COLLECTION dbo.OrderSchema AS N'
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:simpleType name="MyType">
                <xs:restriction base="xs:IDREF"/>
              </xs:simpleType>
              <xs:element name="Root" type="MyType"/>
            </xs:schema>';
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(XmlSchemaCollectionDisallowedTypeKind.IdOrIdRefType, finding.Kind);
    }

    [Fact]
    public void IdAsAttributeType_DoesNotFire()
    {
        var findings = Scan("""
            CREATE XML SCHEMA COLLECTION dbo.OrderSchema AS N'
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:complexType name="MyType">
                <xs:attribute name="a" type="xs:ID"/>
              </xs:complexType>
              <xs:element name="Root" type="MyType"/>
            </xs:schema>';
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void OrdinaryStringElementType_DoesNotFire()
    {
        var findings = Scan("""
            CREATE XML SCHEMA COLLECTION dbo.OrderSchema AS N'
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="Name" type="xs:string"/>
            </xs:schema>';
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void NonDefaultSchemaPrefix_StillDetectsNotation()
    {
        var findings = Scan("""
            CREATE XML SCHEMA COLLECTION dbo.OrderSchema AS N'
            <schema xmlns="http://www.w3.org/2001/XMLSchema" xmlns:tns="urn:test">
              <element name="Format" type="NOTATION"/>
            </schema>';
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(XmlSchemaCollectionDisallowedTypeKind.NotationType, finding.Kind);
    }

    [Fact]
    public void AlterXmlSchemaCollection_WithNotation_Fires()
    {
        var findings = Scan("""
            ALTER XML SCHEMA COLLECTION dbo.OrderSchema ADD N'
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="Format" type="xs:NOTATION"/>
            </xs:schema>';
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(XmlSchemaCollectionDisallowedTypeKind.NotationType, finding.Kind);
    }

    [Fact]
    public void MalformedXml_DoesNotFire()
    {
        var findings = Scan("""
            CREATE XML SCHEMA COLLECTION dbo.OrderSchema AS N'not valid xml <<<';
            """);

        Assert.Empty(findings);
    }
}
