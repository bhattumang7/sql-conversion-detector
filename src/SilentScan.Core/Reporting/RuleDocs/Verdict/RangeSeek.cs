using SilentScan.Core.Reporting.Sarif;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Reporting.RuleDocs.Verdict;

internal static class RangeSeek
{
    public static string RuleId => SarifRuleCatalog.VerdictRuleId(Rules.Verdict.RangeSeek);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            This verdict covers a narrower, less severe case than ScanForced: an implicit
            conversion is still happening on the column side, but for this particular pair of
            types the engine can still generate a dynamic range seek rather than degrading all the
            way to a full scan. This typically happens with numeric type mismatches - an INT
            column compared against a DECIMAL or BIGINT value, for example - where the conversion
            still preserves enough ordering information for the optimizer to bound a seek, just not
            as tightly or as cheaply as a same-type comparison would. It's real, measurable
            overhead, but it is not the same class of problem as a predicate that degrades to a
            full scan - the index is still being used, just less efficiently than it could be.

            This verdict exists specifically so this tool's own output doesn't lump every implicit
            conversion into one severity bucket: a conversion that still lets the engine seek is a
            genuinely different, less urgent finding than one that forces a scan, and conflating
            them would waste a reader's time chasing a scan-level fix for a range-seek-level cost.
            """,
        HowToFixIt: """
            The fix is the same shape as ScanForced's: match the comparison value's declared type
            to the column's own type exactly, so no conversion happens on the column side at all -
            it just matters less urgently here, since the query is already seeking, only somewhat
            less efficiently than it could be.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An INT column compared against a DECIMAL parameter",
                NoncompliantSql: """
                    CREATE TABLE dbo.Inventory
                    (
                        ItemId   INT NOT NULL PRIMARY KEY,
                        Quantity INT NOT NULL
                    );
                    CREATE INDEX IX_Inventory_Quantity ON dbo.Inventory(Quantity);

                    CREATE PROCEDURE dbo.FindLowStock (@threshold DECIMAL(10,2))
                    AS
                    SELECT ItemId
                    FROM dbo.Inventory
                    WHERE Quantity < @threshold;
                    """,
                NoncompliantExplanation: "DECIMAL outranks INT in type precedence, so Quantity is implicitly converted before comparison - the engine can still bound a dynamic range seek for this type pair, but at extra per-row conversion cost the same-type form wouldn't pay.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.FindLowStock (@threshold INT)
                    AS
                    SELECT ItemId
                    FROM dbo.Inventory
                    WHERE Quantity < @threshold;
                    """,
                CompliantExplanation: "The parameter now matches Quantity's own INT type - no conversion is needed, and the seek runs at full efficiency."),
        ]);
}
