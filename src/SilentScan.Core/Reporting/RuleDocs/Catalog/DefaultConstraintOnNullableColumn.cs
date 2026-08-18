using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class DefaultConstraintOnNullableColumn
{
    public static string RuleId => SarifRuleCatalog.DefaultNullableConstraintRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A DEFAULT constraint has exactly one trigger condition: the column is OMITTED entirely
            from the INSERT statement's own column list (or, for a multi-row VALUES/table-valued
            insert, the position is omitted the same way). It does not fire because a value is
            "missing" in any broader sense, and it specifically does not fire just because NULL was
            supplied - an INSERT that names the column and supplies NULL for it stores NULL,
            full stop. The DEFAULT never gets a chance to run, because as far as the engine is
            concerned a value was provided; it simply happens to be NULL.

            This becomes a real, silent problem the moment the column is also nullable. If the
            column were NOT NULL, an explicit NULL would raise a constraint violation immediately,
            which at least surfaces the mismatch between what the caller sent and what was
            intended. Nullable, there's no error at all - the row is inserted, the column holds
            NULL instead of the DEFAULT's value, and nothing in the transaction's outcome indicates
            anything went differently than expected.

            The most common real-world trigger is an ORM or code generator that always emits a full
            column list on INSERT (mapping every property on the entity to every column, in column
            order) rather than omitting columns the entity leaves unset. If the entity's property
            for that column is left at its CLR default (null for a reference type, or explicitly
            unset), the generated INSERT supplies NULL explicitly for a column the DDL intended to
            auto-populate - CreatedAt DEFAULT GETDATE(), for instance - and every such row silently
            gets a NULL timestamp instead of the insert time the DEFAULT was written to capture.
            """,
        HowToFixIt: """
            Make the column NOT NULL if a NULL value was never actually a valid state for it - that
            converts the silent substitution into an immediate, visible constraint violation from
            any caller that supplies NULL explicitly, surfacing the mismatch instead of hiding it.
            If NULL genuinely needs to remain a legal value for the column, stop relying on the
            DEFAULT for callers that might supply NULL explicitly - either have those callers omit
            the column from their INSERT's column list so the DEFAULT actually fires, or set the
            value explicitly in application code instead of depending on the constraint to supply
            it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An ORM's full-column INSERT bypasses the DEFAULT",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        OrderId   INT      NOT NULL PRIMARY KEY,
                        CreatedAt DATETIME NULL CONSTRAINT DF_Orders_CreatedAt DEFAULT (GETDATE())
                    );

                    INSERT INTO dbo.Orders (OrderId, CreatedAt) VALUES (1, NULL);
                    """,
                NoncompliantExplanation: "CreatedAt is named explicitly in the column list with a NULL value supplied, so the DEFAULT never fires - the row is stored with CreatedAt = NULL, not the insert-time timestamp the constraint was written to provide.",
                CompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        OrderId   INT      NOT NULL PRIMARY KEY,
                        CreatedAt DATETIME NOT NULL CONSTRAINT DF_Orders_CreatedAt DEFAULT (GETDATE())
                    );
                    """,
                CompliantExplanation: "With CreatedAt NOT NULL, the same explicit INSERT ... VALUES (1, NULL) now fails immediately with a constraint violation instead of silently storing NULL, surfacing the caller's mismatch instead of masking it."),
        ]);
}
