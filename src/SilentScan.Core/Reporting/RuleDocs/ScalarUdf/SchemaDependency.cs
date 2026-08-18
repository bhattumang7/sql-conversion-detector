using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ScalarUdf;

internal static class SchemaDependency
{
    public static string RuleId => SarifRuleCatalog.ScalarUdfSchemaDependencyRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A scalar UDF called inside a computed column's definition, a `DEFAULT` constraint, or a
            `CHECK` constraint is fundamentally different from every other scalar-UDF finding on
            this page, because it isn't a property of any one query - it's a property of the table
            itself, baked into the schema. Every statement that touches the table pays the cost,
            including ones that never name the column at all: a computed column is (re)evaluated for
            every row an INSERT, UPDATE, or (for a non-persisted computed column) SELECT touches
            unless the specific query explicitly excludes it, a DEFAULT fires on every INSERT that
              doesn't supply the column, and a CHECK constraint's function call runs on every INSERT
            and every UPDATE that could affect the constrained column, whether or not the function
            is provably inlineable at that engine version.

            This is also a genuinely different detection stream from the other scalar-UDF rules on
            this page: those are found by walking a query's predicate, SELECT list, or dependency
            chain through views and TVFs. This one is found by walking the catalog alone - a
            computed column's definition, a default constraint's definition, and a check
            constraint's definition are all recorded as text in `sys.computed_columns`,
            `sys.default_constraints`, and `sys.check_constraints` respectively - so it fires purely
            from the schema, independent of whether any scanned query in the corpus ever references
            the affected table at all. A table can carry this finding on day one, before a single
            query has been written against it.
            """,
        HowToFixIt: """
            For a computed column, replace the function call with the equivalent inline expression
            in the column's own definition, the same rewrite as any other scalar-UDF-in-expression
            finding - and if the column is genuinely expensive to recompute per read, consider
            marking it `PERSISTED` once it no longer depends on a function call, so it's computed
            once at write time instead of at every read. For a DEFAULT or CHECK constraint, the same
            principle applies: express the default value or the validation condition as a plain
            expression in the constraint definition rather than delegating to a function call, so the
            cost paid on every insert/update is an ordinary expression evaluation rather than a
            separate routine invocation.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A computed column defined via a scalar UDF call",
                NoncompliantSql: """
                    CREATE FUNCTION dbo.discount_price(@price DECIMAL(12,2), @discount DECIMAL(12,2))
                    RETURNS DECIMAL(12,2)
                    AS
                    BEGIN
                        RETURN @price * (1 - @discount);
                    END;

                    CREATE TABLE dbo.LineItem
                    (
                        LineItemId      INT           NOT NULL PRIMARY KEY,
                        ExtendedPrice   DECIMAL(12,2) NOT NULL,
                        Discount        DECIMAL(12,2) NOT NULL,
                        DiscountedPrice AS dbo.discount_price(ExtendedPrice, Discount)
                    );
                    """,
                NoncompliantExplanation: "Every INSERT/UPDATE against LineItem - and, since DiscountedPrice isn't PERSISTED, every SELECT that reads it - re-invokes discount_price as a separate routine call unless the engine can prove it's inlineable, purely because of how the column is declared.",
                CompliantSql: """
                    CREATE TABLE dbo.LineItem
                    (
                        LineItemId      INT           NOT NULL PRIMARY KEY,
                        ExtendedPrice   DECIMAL(12,2) NOT NULL,
                        Discount        DECIMAL(12,2) NOT NULL,
                        DiscountedPrice AS (ExtendedPrice * (1 - Discount)) PERSISTED
                    );
                    """,
                CompliantExplanation: "The computed column expression is inlined directly, so there's no function call at all - and marking it PERSISTED means the value is computed once at write time instead of on every read."),
        ]);
}
