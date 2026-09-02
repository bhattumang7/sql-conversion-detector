using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class OpenXmlWithClrTypeRejected
{
    public static string RuleId => SarifRuleCatalog.SchemaWithRejectedTypeRuleId(SilentScan.Core.Predicates.SchemaWithRejectedTypeKind.OpenXmlClrType);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            OPENXML's WITH clause rejects a column declared as a CLR type - geometry, geography, or
            hierarchyid. Confirmed directly against a real SQL Server instance, against a real,
            already-prepared document handle: the row set never returns, failing with Msg 6632
            ("Invalid data type for the column ... . CLR types cannot be used in an OpenXML WITH
            clause.") regardless of the actual XML document's content. TEXT/NTEXT/IMAGE and
            SQL_VARIANT are not affected by this restriction - they work fine in an OPENXML WITH
            schema - so this rule reports only the CLR-type leg.
            """,
        HowToFixIt: """
            Declare the column with a non-CLR type in the WITH schema (its serialized string or
            binary form, for instance) and convert it to the CLR type after OPENXML returns the row.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An OPENXML WITH schema declaring a geometry column never returns rows",
                NoncompliantSql: """
                    DECLARE @docHandle INT;
                    EXEC sp_xml_preparedocument @docHandle OUTPUT, @xml;

                    SELECT *
                    FROM OPENXML(@docHandle, '/Root/Shape', 1)
                    WITH (Boundary geometry);
                    """,
                NoncompliantExplanation: "Boundary is declared geometry - this statement fails with Msg 6632 every time it runs.",
                CompliantSql: """
                    DECLARE @docHandle INT;
                    EXEC sp_xml_preparedocument @docHandle OUTPUT, @xml;

                    SELECT Boundary = geometry::STGeomFromText(BoundaryText, 4326)
                    FROM OPENXML(@docHandle, '/Root/Shape', 1)
                    WITH (BoundaryText VARCHAR(MAX) 'Boundary');
                    """,
                CompliantExplanation: "The WITH schema declares a plain VARCHAR(MAX) column, converted to geometry after OPENXML returns the row."),
        ]);
}

internal static class OpenRowsetWithLegacyTypeRejected
{
    public static string RuleId => SarifRuleCatalog.SchemaWithRejectedTypeRuleId(SilentScan.Core.Predicates.SchemaWithRejectedTypeKind.OpenRowsetLegacyType);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            OPENROWSET(BULK ...)'s inline-schema WITH clause rejects a column declared SQL_VARIANT,
            TEXT, NTEXT, or IMAGE outright. Confirmed directly against a real SQL Server instance:
            the statement fails to compile with Msg 13801 ("TEXT, NTEXT, SQL_VARIANT and IMAGE types
            cannot be used as column types in OPENROWSET function with inline schema.") before the
            source file is ever opened - the check fires identically for CSV and PARQUET sources, and
            even when the file path does not exist, because it is a pure compile-time schema check,
            not a data-conversion error.
            """,
        HowToFixIt: """
            Declare the column as a supported type in the WITH schema - VARCHAR(MAX)/NVARCHAR(MAX)/
            VARBINARY(MAX) in place of the legacy large-object types, or an ordinary scalar type in
            place of SQL_VARIANT - converting further after OPENROWSET returns the row if genuinely
            needed.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An OPENROWSET(BULK ...) inline schema declaring a SQL_VARIANT column never compiles",
                NoncompliantSql: """
                    SELECT *
                    FROM OPENROWSET(BULK 'D:\data\import.csv', FORMAT = 'CSV')
                    WITH (Id INT, Payload SQL_VARIANT) AS Import;
                    """,
                NoncompliantExplanation: "Payload is declared SQL_VARIANT - this statement fails to compile with Msg 13801 every time it runs.",
                CompliantSql: """
                    SELECT *
                    FROM OPENROWSET(BULK 'D:\data\import.csv', FORMAT = 'CSV')
                    WITH (Id INT, Payload NVARCHAR(MAX)) AS Import;
                    """,
                CompliantExplanation: "Payload is declared NVARCHAR(MAX) - a type the inline schema WITH clause supports."),
        ]);
}

internal static class OpenRowsetWithClrTypeRejected
{
    public static string RuleId => SarifRuleCatalog.SchemaWithRejectedTypeRuleId(SilentScan.Core.Predicates.SchemaWithRejectedTypeKind.OpenRowsetClrType);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            OPENROWSET(BULK ...)'s inline-schema WITH clause rejects a column declared as a CLR type
            - geometry, geography, or hierarchyid. Confirmed directly against a real SQL Server
            instance: the statement fails to compile with Msg 13802 ("CLR types cannot be used as
            column types in OPENROWSET function with inline schema.") before the source file is ever
            opened, identically for CSV and PARQUET sources and even when the file path does not
            exist.
            """,
        HowToFixIt: """
            Declare the column with a non-CLR type in the WITH schema (its serialized string or
            binary form, for instance) and convert it after OPENROWSET returns the row.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An OPENROWSET(BULK ...) inline schema declaring a hierarchyid column never compiles",
                NoncompliantSql: """
                    SELECT *
                    FROM OPENROWSET(BULK 'D:\data\import.csv', FORMAT = 'CSV')
                    WITH (Id INT, Path hierarchyid) AS Import;
                    """,
                NoncompliantExplanation: "Path is declared hierarchyid - this statement fails to compile with Msg 13802 every time it runs.",
                CompliantSql: """
                    SELECT Id, Path = CAST(PathText AS hierarchyid)
                    FROM OPENROWSET(BULK 'D:\data\import.csv', FORMAT = 'CSV')
                    WITH (Id INT, PathText VARCHAR(4000)) AS Import;
                    """,
                CompliantExplanation: "The WITH schema declares a plain VARCHAR column, converted to hierarchyid after OPENROWSET returns the row."),
        ]);
}

internal static class OpenRowsetWithXmlRejected
{
    public static string RuleId => SarifRuleCatalog.SchemaWithRejectedTypeRuleId(SilentScan.Core.Predicates.SchemaWithRejectedTypeKind.OpenRowsetXml);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            OPENROWSET(BULK ...)'s inline-schema WITH clause rejects a column declared xml outright.
            Confirmed directly against a real SQL Server instance: the statement fails to compile
            with Msg 13829 ("XML type cannot be used as column type in OPENROWSET function with
            inline schema.") before the source file is ever opened, identically for CSV and PARQUET
            sources and even when the file path does not exist.
            """,
        HowToFixIt: """
            Declare the column as a string type in the WITH schema and parse it into xml after
            OPENROWSET returns the row.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An OPENROWSET(BULK ...) inline schema declaring an xml column never compiles",
                NoncompliantSql: """
                    SELECT *
                    FROM OPENROWSET(BULK 'D:\data\import.csv', FORMAT = 'CSV')
                    WITH (Id INT, Payload XML) AS Import;
                    """,
                NoncompliantExplanation: "Payload is declared XML - this statement fails to compile with Msg 13829 every time it runs.",
                CompliantSql: """
                    SELECT Id, Payload = CAST(PayloadText AS XML)
                    FROM OPENROWSET(BULK 'D:\data\import.csv', FORMAT = 'CSV')
                    WITH (Id INT, PayloadText NVARCHAR(MAX)) AS Import;
                    """,
                CompliantExplanation: "The WITH schema declares a plain NVARCHAR(MAX) column, cast to xml after OPENROWSET returns the row."),
        ]);
}
