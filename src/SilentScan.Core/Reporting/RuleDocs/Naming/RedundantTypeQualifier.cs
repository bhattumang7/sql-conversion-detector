using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Naming;

internal static class RedundantTypeQualifier
{
    public static string RuleId => SarifRuleCatalog.NamingRedundantTypeQualifierRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A user-defined type reference (a parameter, a variable's DECLARE, a table-type
            parameter) can carry a schema qualifier the same way a table reference can - `@p
            dbo.MyType READONLY` instead of just `@p MyType READONLY`. An unqualified type name
            resolves via the connecting principal's own default schema first, exactly like an
            unqualified table or view reference - so a `dbo.` qualifier is only genuinely redundant
            once the catalog confirms no other schema in the scanned database defines a same-named
            type. This check only reports the finding once that's been confirmed.

            This check is deliberately narrow: it flags an explicit `dbo.` qualifier only, never any
            other schema name, and only once the catalog rules out a same-named type existing
            elsewhere. A qualifier naming some other schema might still be genuinely load-bearing
            regardless of catalog data - whether it's redundant also depends on the connecting
            principal's own actual default schema, which this pass has no way to know, so flagging
            any schema other than `dbo` would risk a false positive. Flagging `dbo` without that
            catalog confirmation would risk exactly the same false positive, whenever a same-named
            type exists in another schema and the code later runs under a principal whose default
            schema is that other one.
            """,
        HowToFixIt: """
            Drop the redundant "dbo." schema qualifier from the data type reference - the catalog
            confirms no other schema defines a same-named type, so it resolves identically without
            it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A redundant dbo. qualifier on a parameter's type",
                NoncompliantSql: "CREATE PROCEDURE dbo.P (@p dbo.MyType READONLY) AS BEGIN SELECT 1; END",
                NoncompliantExplanation: "dbo.MyType resolves to exactly the same type as MyType alone would - the qualifier adds nothing but noise.",
                CompliantSql: "CREATE PROCEDURE dbo.P (@p MyType READONLY) AS BEGIN SELECT 1; END",
                CompliantExplanation: "MyType resolves the same way without the redundant qualifier."),
        ]);
}
