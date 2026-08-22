using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class XmlOperandNotComparable
{
    public static string RuleId => SarifRuleCatalog.OperandComparabilityRuleId(SilentScan.Core.Predicates.OperandComparabilityFindingKind.Xml);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The xml data type is not comparable at all - not to another xml value, and not to a
            string literal, a variable, or anything else. SQL Server's binder rejects the statement
            outright at compile time, before any row is ever touched: an xml column compared with
            another xml column fails with Msg 305 ("The XML data type cannot be compared or sorted,
            except when using the IS NULL operator"), the same message ORDER BY/GROUP BY/IN/BETWEEN/
            NULLIF against an xml column produce; compared against a differently-typed operand (a
            string literal, for instance) the engine instead raises the narrower Msg 402 for the
            identical reason. SELECT DISTINCT over an xml column fails its own way, Msg 421 ("...
            cannot be selected as DISTINCT because it is not comparable"). Every one of these was
            confirmed directly against a real SQL Server instance, not assumed from documentation.

            IS NULL/IS NOT NULL are unaffected (comparability isn't at stake - null-ness is a
            distinct, always-legal question), and so is a bare CASE/COALESCE branch: picking one of
            several xml-typed branches never compares them against each other, and neither does this
            rule flag it.
            """,
        HowToFixIt: """
            Remove the xml column from the comparison/ordering/grouping/DISTINCT position. If the
            comparison is genuinely needed, shred the value first with .value()/.exist()/.nodes() (or
            an OPENXML/nodes() cross apply) down to a comparable scalar type, and compare that
            instead of the xml value itself.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Comparing two xml columns never compiles",
                NoncompliantSql: """
                    CREATE TABLE dbo.Document
                    (
                        DocumentId INT NOT NULL PRIMARY KEY,
                        Payload    XML NOT NULL,
                        Template   XML NOT NULL
                    );

                    SELECT DocumentId
                    FROM dbo.Document
                    WHERE Payload = Template;
                    """,
                NoncompliantExplanation: "Payload and Template are both xml - this statement fails to compile with Msg 305 every time it runs.",
                CompliantSql: """
                    SELECT DocumentId
                    FROM dbo.Document
                    WHERE Payload.value('(/root/@id)[1]', 'int') = Template.value('(/root/@id)[1]', 'int');
                    """,
                CompliantExplanation: "Both sides are shredded down to an INT with .value() first - a genuinely comparable scalar type, not the raw xml value."),
        ]);
}

internal static class LegacyLobOperandNotComparable
{
    public static string RuleId => SarifRuleCatalog.OperandComparabilityRuleId(SilentScan.Core.Predicates.OperandComparabilityFindingKind.LegacyLargeObject);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            TEXT, NTEXT, and IMAGE - the pre-VARCHAR(MAX)/NVARCHAR(MAX)/VARBINARY(MAX) large-object
            types, deprecated but still present in older schemas - carry their own comparability
            restriction, distinct from (and stricter than) the ordinary type-precedence rules every
            other type follows. Confirmed directly against a real SQL Server instance: a TEXT/NTEXT/
            IMAGE column used in =, &lt;&gt;, a range operator, an IN list, BETWEEN, or NULLIF fails
            to compile with Msg 402 ("The data types ... are incompatible in the ... operator");
            referenced in ORDER BY or GROUP BY it instead raises Msg 306 ("The text, ntext, and image
            data types cannot be compared or sorted, except when using IS NULL or LIKE operator") -
            the engine's own error text names the two exceptions explicitly, and both were confirmed
            to actually compile: LIKE against a TEXT/NTEXT column works, and so does IS NULL. SELECT
            DISTINCT over one of these columns fails its own way (Msg 421), the identical restriction
            xml carries.
            """,
        HowToFixIt: """
            Migrate the column to VARCHAR(MAX)/NVARCHAR(MAX)/VARBINARY(MAX) - the modern large-object
            types support ordinary comparison, ordering, and grouping with none of this restriction.
            Where migrating isn't immediately possible, remove the column from the comparison/
            ordering/grouping/DISTINCT position, or rewrite the comparison as a LIKE pattern instead.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Sorting by a TEXT column never compiles",
                NoncompliantSql: """
                    CREATE TABLE dbo.Article
                    (
                        ArticleId INT NOT NULL PRIMARY KEY,
                        Body      TEXT NOT NULL
                    );

                    SELECT ArticleId
                    FROM dbo.Article
                    ORDER BY Body;
                    """,
                NoncompliantExplanation: "Body is TEXT - this statement fails to compile with Msg 306 every time it runs.",
                CompliantSql: """
                    ALTER TABLE dbo.Article ALTER COLUMN Body VARCHAR(MAX) NOT NULL;

                    SELECT ArticleId
                    FROM dbo.Article
                    ORDER BY Body;
                    """,
                CompliantExplanation: "Body is migrated to VARCHAR(MAX) - the modern large-object type has no comparison/ordering restriction."),
        ]);
}
