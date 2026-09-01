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
            cannot be selected as DISTINCT because it is not comparable"), and a window function's
            own PARTITION BY clause fails yet another way, Msg 249 ("The type ... is not comparable.
            It cannot be used in the PARTITION BY clause"). Every one of these was confirmed directly
            against a real SQL Server instance, not assumed from documentation.

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

internal static class JsonOperandNotComparable
{
    public static string RuleId => SarifRuleCatalog.OperandComparabilityRuleId(SilentScan.Core.Predicates.OperandComparabilityFindingKind.Json);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The json data type carries the identical comparability restriction xml does. SQL
            Server's binder rejects the statement outright at compile time, before any row is ever
            touched: a json column compared with another json column fails with Msg 13636 ("The
            JSON data type cannot be compared or sorted, except when using the IS NULL operator"),
            the same message ORDER BY/GROUP BY/IN/BETWEEN/NULLIF against a json column produce.
            SELECT DISTINCT over a json column fails its own way, Msg 421 ("The json data type
            cannot be selected as DISTINCT because it is not comparable"), and a window function's
            own PARTITION BY clause rejects it the same way as ORDER BY/GROUP BY. All were confirmed
            directly against a real SQL Server instance, not assumed from documentation.

            IS NULL/IS NOT NULL are unaffected (comparability isn't at stake - null-ness is a
            distinct, always-legal question), and so is a bare CASE/COALESCE branch: picking one of
            several json-typed branches never compares them against each other, and neither does
            this rule flag it.
            """,
        HowToFixIt: """
            Remove the json column from the comparison/ordering/grouping/DISTINCT position. If the
            comparison is genuinely needed, extract a comparable scalar value first with
            JSON_VALUE(), and compare that instead of the raw json value itself.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Comparing two json columns never compiles",
                NoncompliantSql: """
                    CREATE TABLE dbo.Document
                    (
                        DocumentId INT NOT NULL PRIMARY KEY,
                        Payload    JSON NOT NULL,
                        Template   JSON NOT NULL
                    );

                    SELECT DocumentId
                    FROM dbo.Document
                    WHERE Payload = Template;
                    """,
                NoncompliantExplanation: "Payload and Template are both json - this statement fails to compile with Msg 13636 every time it runs.",
                CompliantSql: """
                    SELECT DocumentId
                    FROM dbo.Document
                    WHERE JSON_VALUE(Payload, '$.id') = JSON_VALUE(Template, '$.id');
                    """,
                CompliantExplanation: "Both sides are extracted down to a scalar value with JSON_VALUE() first - a genuinely comparable value, not the raw json value."),
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
            referenced in ORDER BY, GROUP BY, or a window function's own PARTITION BY clause it
            instead raises Msg 306 ("The text, ntext, and image data types cannot be compared or
            sorted, except when using IS NULL or LIKE operator") - the engine's own error text names
            the two exceptions explicitly, and both were confirmed to actually compile: LIKE against
            a TEXT/NTEXT column works, and so does IS NULL. SELECT DISTINCT over one of these columns
            fails its own way (Msg 421), the identical restriction xml carries.
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

internal static class SpatialOperandNotComparable
{
    public static string RuleId => SarifRuleCatalog.OperandComparabilityRuleId(SilentScan.Core.Predicates.OperandComparabilityFindingKind.Spatial);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            geometry and geography carry no comparison operator at all, not even against another
            value of the identical spatial type - unlike xml/json/legacy large-object types, this
            isn't restricted to ordering/grouping, it is every relational operator SQL Server has.
            Confirmed directly against a real SQL Server instance: comparing two geometry values with
            = fails with Msg 403 ("Invalid operator for data type. Operator equals equal to, type
            equals geometry"), and so does comparing a geometry value against an ordinary scalar like
            an int - the message and mechanism are identical regardless of what sits on the other
            side. geography fails the identical way. Referenced in ORDER BY, GROUP BY, or a window
            function's own PARTITION BY clause, the engine instead raises Msg 249 ("The type ... is
            not comparable. It cannot be used in the ... clause"); SELECT DISTINCT over a spatial
            column fails its own way, Msg 421, the same restriction xml/json carry.

            IS NULL/IS NOT NULL are unaffected (comparability isn't at stake), and so is a bare CASE/
            COALESCE branch: picking one of several spatial-typed branches never compares them
            against each other, and neither does this rule flag it.
            """,
        HowToFixIt: """
            Remove the geometry/geography column from the comparison/ordering/grouping/partitioning/
            DISTINCT position. If the comparison is genuinely needed, use a spatial method that
            returns a comparable scalar instead - .STEquals() for equality, .STDistance() for
            ordering by proximity - and compare that instead of the raw spatial value itself.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Comparing two geometry columns never compiles",
                NoncompliantSql: """
                    CREATE TABLE dbo.Parcel
                    (
                        ParcelId INT NOT NULL PRIMARY KEY,
                        Boundary geometry NOT NULL,
                        Prior    geometry NOT NULL
                    );

                    SELECT ParcelId
                    FROM dbo.Parcel
                    WHERE Boundary = Prior;
                    """,
                NoncompliantExplanation: "Boundary and Prior are both geometry - this statement fails to compile with Msg 403 every time it runs.",
                CompliantSql: """
                    SELECT ParcelId
                    FROM dbo.Parcel
                    WHERE Boundary.STEquals(Prior) = 1;
                    """,
                CompliantExplanation: "STEquals() returns a bit - a genuinely comparable scalar, not the raw geometry value."),
        ]);
}
