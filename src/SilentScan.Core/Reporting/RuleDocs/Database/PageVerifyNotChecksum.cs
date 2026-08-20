using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Database;

internal static class PageVerifyNotChecksum
{
    public static string RuleId => SarifRuleCatalog.DatabaseConfigurationRuleId(DatabaseConfigurationFindingKind.PageVerifyNotChecksum);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `PAGE_VERIFY` controls how SQL Server detects storage-level page corruption - disk,
            controller, or filesystem errors that silently flip bits underneath the database. Set to
            anything other than `CHECKSUM` (either the legacy `TORN_PAGE_DETECTION` or `NONE`
            entirely), the engine has a weaker or nonexistent way to notice that a page's contents no
            longer match what it wrote - corruption goes undetected until a much later, far
            harder-to-diagnose failure, often surfacing as a mysterious query error or a corrupt
            backup restore rather than an immediate, actionable signal at the moment the damage
            actually happened.

            This is a database-level configuration fact, read once per scan directly from
            `sys.databases` - not a per-module or per-query concern the way most of this tool's other
            findings are, and only available when scanning a live, connected target (there is no
            file-mode equivalent of "the database's own current configuration").
            """,
        HowToFixIt: """
            Set PAGE_VERIFY to CHECKSUM.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A database with PAGE_VERIFY left at the legacy setting",
                NoncompliantSql: """
                    ALTER DATABASE CURRENT SET PAGE_VERIFY TORN_PAGE_DETECTION;
                    """,
                NoncompliantExplanation: "TORN_PAGE_DETECTION only catches a narrower class of corruption than CHECKSUM does - real storage-level corruption can go undetected until a much later failure.",
                CompliantSql: """
                    ALTER DATABASE CURRENT SET PAGE_VERIFY CHECKSUM;
                    """,
                CompliantExplanation: "CHECKSUM gives the engine the strongest built-in detection of storage-level page corruption available."),
        ]);
}
