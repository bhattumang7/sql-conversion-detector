using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class ForXmlExplicitInlineXsd
{
    public static string RuleId => SarifRuleCatalog.ForXmlExplicitInlineXsdRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            FOR XML EXPLICIT and XMLSCHEMA are each individually well-supported FOR XML directives, but
            combining them on the same query never compiles. Oracle-confirmed against a real engine: a
            FOR XML EXPLICIT query that also specifies XMLSCHEMA fails with "'Inline XSD for FOR XML
            EXPLICIT' is not yet implemented" - a genuine, decidable-purely-from-the-statement's-own-
            option-list compile-time reject, though it surfaces as an unimplemented-feature message rather
            than a documented "these two options can't combine" error. Every other FOR XML mode
            (RAW/AUTO/PATH) supports XMLSCHEMA without issue - the restriction is specific to EXPLICIT.
            """,
        HowToFixIt: """
            Drop XMLSCHEMA from a FOR XML EXPLICIT query, or switch the query to FOR XML AUTO, PATH, or
            RAW if an inline XSD is actually required.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "FOR XML EXPLICIT combined with XMLSCHEMA never compiles",
                NoncompliantSql: """
                    SELECT 1 AS Tag, NULL AS Parent, name AS [Row!1!name]
                    FROM sys.objects
                    FOR XML EXPLICIT, XMLSCHEMA;
                    """,
                NoncompliantExplanation: "Fails to compile - inline XSD generation is not implemented for FOR XML EXPLICIT.",
                CompliantSql: """
                    SELECT 1 AS Tag, NULL AS Parent, name AS [Row!1!name]
                    FROM sys.objects
                    FOR XML EXPLICIT;
                    """,
                CompliantExplanation: "Dropping XMLSCHEMA compiles - EXPLICIT mode without an inline XSD is fully supported."),
        ]);
}
