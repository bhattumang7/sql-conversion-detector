using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.CreateDatabase;

internal static class OptionConflict
{
    public static string RuleId => SarifRuleCatalog.CreateDatabaseOptionConflictRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `CONTAINMENT = PARTIAL` and `CATALOG_COLLATION` are mutually exclusive on `CREATE
            DATABASE`, decidable purely from the statement's own option list.

            Oracle-confirmed (Docker SQL Server 2022): `CREATE DATABASE db CONTAINMENT = PARTIAL
            WITH CATALOG_COLLATION = DATABASE_DEFAULT` always fails (Msg 12845, "cannot specify
            both CONTAINMENT = PARTIAL and CATALOG_COLLATION"). `CATALOG_COLLATION` alone -
            `CONTAINMENT` omitted or explicitly `CONTAINMENT = NONE` - is unaffected; the conflict
            is specific to combining `CATALOG_COLLATION` with the `PARTIAL` containment level.
            """,
        HowToFixIt: "Drop CATALOG_COLLATION if CONTAINMENT = PARTIAL is genuinely needed, or drop CONTAINMENT = PARTIAL if the custom catalog collation is the actual requirement.",
        Examples:
        [
            new RuleDocExample(
                Title: "CONTAINMENT = PARTIAL combined with CATALOG_COLLATION",
                NoncompliantSql: "CREATE DATABASE SomeDatabase CONTAINMENT = PARTIAL WITH CATALOG_COLLATION = DATABASE_DEFAULT;",
                NoncompliantExplanation: "CATALOG_COLLATION can never be combined with CONTAINMENT = PARTIAL, so this statement always fails with Msg 12845."),
        ]);
}
