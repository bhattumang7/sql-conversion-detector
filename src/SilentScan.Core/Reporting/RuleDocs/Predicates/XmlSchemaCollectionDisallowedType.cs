using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class XmlSchemaCollectionNotationType
{
    public static string RuleId => SarifRuleCatalog.XmlSchemaCollectionDisallowedTypeRuleId(SilentScan.Core.Predicates.XmlSchemaCollectionDisallowedTypeKind.NotationType);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SQL Server's XML schema collection support does not implement the built-in XML Schema
            type `NOTATION` anywhere in a schema - as an element's type, an attribute's type, or an
            `xs:extension`/`xs:restriction` base. Confirmed directly against a real SQL Server
            instance: `CREATE XML SCHEMA COLLECTION` (or `ALTER ... ADD`) fails with Msg 9337 ("The
            XML Schema type 'NOTATION' is not supported.") the moment the inline XSD text
            references it anywhere, regardless of whether the schema is otherwise valid XSD.

            The check is namespace-aware, not text-matching on a literal `xs:` prefix - a schema
            that binds the XML Schema namespace to a different prefix (or uses it as the default
            namespace) is still recognized.
            """,
        HowToFixIt: """
            Remove the NOTATION type from the schema - model the value as a string, or as an
            enumeration of `xs:string` values, instead.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An attribute typed xs:NOTATION never registers",
                NoncompliantSql: """
                    CREATE XML SCHEMA COLLECTION dbo.OrderSchema AS N'
                    <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
                      <xs:complexType name="Order">
                        <xs:attribute name="Format" type="xs:NOTATION"/>
                      </xs:complexType>
                    </xs:schema>';
                    -- Fails: Msg 9337, the XML Schema type NOTATION is not supported.
                    """,
                NoncompliantExplanation: "Format is declared xs:NOTATION - this schema collection never registers, every time this statement runs.",
                CompliantSql: """
                    CREATE XML SCHEMA COLLECTION dbo.OrderSchema AS N'
                    <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
                      <xs:complexType name="Order">
                        <xs:attribute name="Format" type="xs:string"/>
                      </xs:complexType>
                    </xs:schema>';
                    """,
                CompliantExplanation: "Format is declared xs:string - the schema collection registers."),
        ]);
}

internal static class XmlSchemaCollectionIdOrIdRefType
{
    public static string RuleId => SarifRuleCatalog.XmlSchemaCollectionDisallowedTypeRuleId(SilentScan.Core.Predicates.XmlSchemaCollectionDisallowedTypeKind.IdOrIdRefType);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SQL Server's XML schema collection support does not permit the built-in XML Schema
            types `ID`/`IDREF` (or a type derived from either) to be used as an `xs:element`'s own
            declared type, or as the `base` of an `xs:extension`/`xs:restriction`. Confirmed
            directly against a real SQL Server instance: `CREATE XML SCHEMA COLLECTION` fails with
            Msg 6995 whenever an element's type - directly, or through a named simple/complex type
            that derives from `ID`/`IDREF` by extension or restriction - resolves to one of these
            built-ins.

            An `xs:attribute` declared `type="xs:ID"`/`"xs:IDREF"` is unaffected - that is the
            ordinary, expected use of these types in XSD, and is confirmed to register fine. Only
            an element's own type (or an extension/restriction base) triggers the failure.
            """,
        HowToFixIt: """
            Give the element a different declared type - a string-based simple type covers most
            XML DML/element-identity use cases - instead of the built-in `ID`/`IDREF` type or a
            type derived from it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An element typed xs:IDREF never registers",
                NoncompliantSql: """
                    CREATE XML SCHEMA COLLECTION dbo.OrderSchema AS N'
                    <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
                      <xs:element name="CustomerRef" type="xs:IDREF"/>
                    </xs:schema>';
                    -- Fails: Msg 6995, ID/IDREF (or a type derived from them) cannot be used as
                    -- the type of an element.
                    """,
                NoncompliantExplanation: "CustomerRef is declared xs:IDREF as its own element type - this schema collection never registers, every time this statement runs.",
                CompliantSql: """
                    CREATE XML SCHEMA COLLECTION dbo.OrderSchema AS N'
                    <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
                      <xs:element name="CustomerRef" type="xs:string"/>
                    </xs:schema>';
                    """,
                CompliantExplanation: "CustomerRef is declared xs:string - the schema collection registers."),
        ]);
}
