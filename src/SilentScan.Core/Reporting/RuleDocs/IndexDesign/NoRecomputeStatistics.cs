using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class NoRecomputeStatistics
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.NoRecomputeStatistics);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A statistics object explicitly created or altered `WITH NORECOMPUTE` never gets
            refreshed by the engine's own automatic statistics-update maintenance - it drifts
            silently stale as the table's data changes, with nothing in the engine's normal
            operation ever correcting it again. That's the actual, provable mechanism this catalog
            flag reports: not "statistics ARE stale right now" (a live data-state fact this pass
            structurally cannot see), but "this object's statistics can never self-correct going
            forward, by its own explicit configuration."

            Stale statistics feed directly into the cardinality estimator's row-count guesses, which
            drive nearly every plan choice the optimizer makes - a NORECOMPUTE statistics object left
            in place indefinitely is a standing risk that plans built against it drift further from
            reality the longer the table keeps changing, with no automatic correction to rely on.
            """,
        HowToFixIt: """
            Remove the NORECOMPUTE option so the engine's automatic statistics maintenance keeps this
            object refreshed.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Statistics explicitly marked to never auto-recompute",
                NoncompliantSql: """
                    CREATE STATISTICS Stats_Orders_Status
                        ON dbo.Orders (Status)
                        WITH NORECOMPUTE;
                    """,
                NoncompliantExplanation: "NORECOMPUTE disables the engine's automatic statistics maintenance for this specific object - as dbo.Orders keeps changing, this statistics object never self-corrects, and every plan the optimizer builds against it drifts further from reality.",
                CompliantSql: """
                    CREATE STATISTICS Stats_Orders_Status
                        ON dbo.Orders (Status);
                    """,
                CompliantExplanation: "Without NORECOMPUTE, the engine's own automatic statistics maintenance keeps this object refreshed as the table's data changes."),
        ]);
}
