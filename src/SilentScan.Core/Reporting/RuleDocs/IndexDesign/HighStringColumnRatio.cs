using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class HighStringColumnRatio
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.HighStringColumnRatio);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A table with at least 5 columns where 80% or more are string-family typed often
            correlates with under-typed data - dates, numbers, or a fixed set of enum-like values
            stored as free text with no CHECK constraint or foreign key narrowing what's actually
            allowed - but this pass cannot confirm that story for any specific column, only report
            the ratio itself. The same "listed for completeness" framing as the sibling wide-table
            and high-nullable-ratio rules, reported at Low confidence always.

            Calibrated against the same real survey as the nullable-ratio sibling: of 835 real
            tables with at least 5 columns, only 9 crossed this 80% string-ratio threshold - kept
            rare enough to stay a meaningful signal rather than firing on every table that happens
            to store a lot of names and descriptions.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A table where most columns are string-typed",
                NoncompliantSql: """
                    CREATE TABLE dbo.Shipment
                    (
                        Id INT NOT NULL PRIMARY KEY,
                        Status VARCHAR(20) NOT NULL,
                        Carrier VARCHAR(50) NOT NULL,
                        ShipDate VARCHAR(10) NOT NULL,
                        DeliveredDate VARCHAR(10) NULL
                    );
                    """,
                NoncompliantExplanation: "4 of 5 columns (80%) are string-typed, including ShipDate/DeliveredDate stored as VARCHAR(10) rather than a real date type - a common under-typing pattern this ratio is a proxy signal for, though it cannot confirm the specific mistake."),
        ]);
}
