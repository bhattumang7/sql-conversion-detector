using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class WideTable
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.WideTable);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A table with an unusually large column count (35 or more) or an unusually large
            estimated non-LOB row width (over 2000 bytes, summed using this catalog's own
            per-column byte-width model - a LOB/MAX/unresolved column contributes nothing to that
            sum rather than a guessed-at figure, so the estimate is always a safe lower bound) is a
            data-modeling signal worth a second look at normalization or hot/cold column
            separation, not a specific, provable defect this pass can point at.

            This is genuinely the lowest-precision finding in this whole family - listed for
            completeness rather than as a priority, reported at Low confidence always. A wide table
            is sometimes exactly the right design (a genuinely wide, flat reporting/staging table),
            so this finding is a prompt to reconsider, never a claim that the table is wrong.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A table with an unusually large column count",
                NoncompliantSql: """
                    CREATE TABLE dbo.CustomerProfile
                    (
                        Id INT NOT NULL PRIMARY KEY,
                        FirstName VARCHAR(50), LastName VARCHAR(50), MiddleName VARCHAR(50),
                        AddressLine1 VARCHAR(100), AddressLine2 VARCHAR(100), City VARCHAR(50),
                        -- ... 30 more columns covering billing, shipping, preferences, marketing opt-ins ...
                        LastLoginAt DATETIME2
                    );
                    """,
                NoncompliantExplanation: "35 or more columns on one table is a real signal that several optional sub-entities (billing, shipping, preferences, marketing) may have been crammed into a single row rather than split into their own related tables."),
        ]);
}
