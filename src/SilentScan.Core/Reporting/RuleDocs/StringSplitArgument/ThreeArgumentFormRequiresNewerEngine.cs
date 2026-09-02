using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.StringSplitArgument;

internal static class ThreeArgumentFormRequiresNewerEngine
{
    public static string RuleId => SarifRuleCatalog.StringSplitArgumentRuleId(StringSplitArgumentFindingKind.ThreeArgumentFormRequiresNewerEngine);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            STRING_SPLIT's 3-argument form (the enable_ordinal switch that adds the ordinal output
            column) shipped in SQL Server 2022. This was probed directly against real engines: on SQL
            Server 2019 (engine major version 15), any call passing a third argument - regardless of
            its value, including a literal 0, 1, or even NULL - raises Msg 8144 ("Procedure or function
            STRING_SPLIT has too many arguments specified") at compile/bind time, before a single row
            is read.

            The gate is the connected engine instance's own major version (read live via
            SERVERPROPERTY('ProductMajorVersion')), not the database's compatibility level - a SQL
            Server 2022 engine still accepts the 3-argument form with the database's compatibility
            level dropped to 150, oracle-confirmed. Only scan runs against a live, connected database
            can evaluate this rule; the engine version is not otherwise present in T-SQL source.
            """,
        HowToFixIt: """
            Drop the third argument if the target engine is older than SQL Server 2022, or upgrade the
            connected instance to SQL Server 2022 or later.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "STRING_SPLIT's 3-argument form against an engine older than SQL Server 2022",
                NoncompliantSql: "SELECT value, ordinal FROM STRING_SPLIT('a,b', ',', 1);",
                NoncompliantExplanation: "Against a connected engine reporting major version below 16, the 3-argument form does not exist - the call raises Msg 8144 before any row is read.",
                CompliantSql: "SELECT value FROM STRING_SPLIT('a,b', ',');",
                CompliantExplanation: "The 2-argument form has been supported since SQL Server 2016 and works on every version this tool targets."),
        ]);
}
