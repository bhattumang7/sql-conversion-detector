using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class CrossTableFkTypeDrift
{
    public static string RuleId => SarifRuleCatalog.CrossTableTypeDriftRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A foreign key relationship declares, by definition, that the referencing column(s) hold
            values drawn from the referenced column(s) - the two are meant to be the same kind of
            value. SQL Server doesn't require the declared types (or, for string types, the
            collations) on the two sides of a foreign key to actually match for the constraint to be
            created; it only requires that the referencing column's type be implicitly convertible to
            the referenced column's type. That's a much weaker guarantee than "the types are the
            same," and it means a foreign key can sit in the schema, fully enforced and passing every
            insert/update, while its two sides carry genuinely different declared types - an INT
            primary key referenced by a BIGINT foreign key column, say, or a VARCHAR primary key
            referenced by an NVARCHAR one.

            That mismatch is a conversion seed on every join that follows the relationship: whenever
            a query joins the two tables on this foreign key - which is, definitionally, the
            relationship's whole purpose - one side's values have to be implicitly converted to
            match the other's type before the comparison runs, and whichever side is the one being
            converted loses the ability to seek an index on it. This is detected from the catalog
            alone (`sys.foreign_key_columns` joined back to `sys.columns` on both the parent and
            child tables), independent of whether any query in the scanned corpus actually performs
            the join yet - the seed is real the moment the foreign key and the type drift both exist,
            whether or not it's been triggered by a query yet.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A foreign key column with a wider type than its referenced primary key",
                NoncompliantSql: """
                    CREATE TABLE dbo.Customers
                    (
                        CustomerId INT NOT NULL PRIMARY KEY
                    );
                    CREATE TABLE dbo.Orders
                    (
                        OrderId    INT    NOT NULL PRIMARY KEY,
                        CustomerId BIGINT NOT NULL
                            REFERENCES dbo.Customers(CustomerId)
                    );
                    """,
                NoncompliantExplanation: "Orders.CustomerId is BIGINT while Customers.CustomerId is INT - the foreign key is still valid (INT converts implicitly to BIGINT), but BIGINT outranks INT in SQL Server's type precedence, so every join on this relationship implicitly converts Customers.CustomerId to BIGINT before comparing, and the primary key's own index can't be seeked through that conversion.",
                CompliantSql: """
                    CREATE TABLE dbo.Customers
                    (
                        CustomerId INT NOT NULL PRIMARY KEY
                    );
                    CREATE TABLE dbo.Orders
                    (
                        OrderId    INT NOT NULL PRIMARY KEY,
                        CustomerId INT NOT NULL
                            REFERENCES dbo.Customers(CustomerId)
                    );
                    """,
                CompliantExplanation: "Both sides of the relationship declare the same type - a join on CustomerId needs no conversion on either side, and an index on either column can be seeked directly."),
        ]);
}
