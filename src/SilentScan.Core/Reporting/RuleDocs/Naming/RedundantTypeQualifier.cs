using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Naming;

internal static class RedundantTypeQualifier
{
    public static string RuleId => SarifRuleCatalog.NamingRedundantTypeQualifierRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A user-defined type reference (a parameter, a variable's DECLARE, a table-type
            parameter) can carry a schema qualifier the same way a table reference can - `@p
            dbo.MyType READONLY` instead of just `@p MyType READONLY`. When that qualifier names
            `dbo` - the default schema this codebase treats as the baseline everywhere else - it
            adds nothing: the type would resolve to the exact same object without it, since `dbo` is
            already where an unqualified type name resolves for the overwhelming majority of
            databases. The qualifier only adds visual noise and couples the declaration to a schema
            name it doesn't actually need to state.

            This check is deliberately narrow: it flags an explicit `dbo.` qualifier only, never any
            other schema name. A qualifier naming some other schema might be genuinely load-bearing -
            whether it's redundant depends on the connecting principal's own actual default schema,
            which this static, catalog-free pass has no way to know, so flagging any schema other
            than the one universally-safe case would risk a real false positive in a multi-schema
            database.
            """,
        HowToFixIt: """
            Drop the redundant "dbo." schema qualifier from the data type reference - the type
            resolves identically without it.
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
