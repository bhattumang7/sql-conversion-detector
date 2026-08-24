using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DeprecatedSyntax;

internal static class LegacySystemCompatibilityView
{
    public static string RuleId => SarifRuleCatalog.DeprecatedSyntaxRuleId(DeprecatedSyntaxFindingKind.LegacySystemCompatibilityView);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A reference to a pre-SQL-Server-2005 system compatibility view, retained only for
            backward compatibility and missing columns/rows the real sys.* catalog view exposes.
            Code that reads it silently misses data a reader would expect to see from the catalog.
            """,
        HowToFixIt: "Reference the real sys.* catalog view instead of the legacy compatibility view.",
        Examples:
        [
            new RuleDocExample(
                Title: "A query against a legacy compatibility view",
                NoncompliantSql: "SELECT name FROM sysobjects WHERE type = 'U';",
                NoncompliantExplanation: "sysobjects is a pre-2005 compatibility view that exposes fewer columns and less accurate metadata than the real catalog view.",
                CompliantSql: "SELECT name FROM sys.objects WHERE type = 'U';",
                CompliantExplanation: "sys.objects is the real catalog view, with the full set of columns and current metadata."),
        ]);
}
