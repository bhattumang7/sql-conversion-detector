using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class HighNullableColumnRatio
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.HighNullableColumnRatio);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A table with at least 5 columns where 80% or more of them are nullable often correlates
            with an overloaded table - several optional sub-entities crammed into one row instead of
            being split into their own related tables joined only where they actually apply - but
            this pass cannot confirm that story for any specific column, only report the ratio
            itself. The same "listed for completeness" framing as the sibling wide-table rule,
            reported at Low confidence always.

            These calibration thresholds come from a real survey of this project's own local
            production-shaped test database: of 835 real tables with at least 5 columns, only 33
            crossed this 80% nullable-ratio threshold, keeping the finding rare enough to stay
            meaningful rather than firing on every ordinary optional-field table.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A table where most columns are nullable",
                NoncompliantSql: """
                    CREATE TABLE dbo.Customer
                    (
                        Id INT NOT NULL PRIMARY KEY,
                        LoyaltyTier VARCHAR(20) NULL,
                        LoyaltyPointsBalance INT NULL,
                        NewsletterOptIn BIT NULL,
                        PreferredContactMethod VARCHAR(20) NULL
                    );
                    """,
                NoncompliantExplanation: "4 of 5 columns (80%) are nullable - the loyalty-program and newsletter columns look like they might belong in their own related tables rather than sitting mostly-empty on every customer row."),
        ]);
}
