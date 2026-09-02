using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class PartitionRebuildNumberExceedsCeiling
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.PartitionRebuildNumberExceedsCeiling);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SQL Server caps every partitioned table or index at 15000 partitions, regardless of
            how its partition function or scheme is defined. ALTER TABLE ... REBUILD PARTITION = n
            and ALTER INDEX ... REBUILD PARTITION = n both validate their partition number against
            this ceiling before looking at the target table's own partition scheme at all - oracle-
            confirmed Msg 7722, "Invalid partition number N specified for table '...', partition
            number can range from 1 to 15000." A number above 15000 fails unconditionally: it is
            not possible for any table, on any scheme, to ever have that many partitions, so the
            statement cannot succeed no matter what the target table's own partitioning looks like.

            Because this ceiling is a fixed engine constant rather than a fact about the specific
            table, a compile-time-foldable partition number above it is decidable from the literal
            alone, with no catalog lookup needed.
            """,
        HowToFixIt: """
            Use a partition number no greater than 15000 - no table can have more partitions than
            that. If the number was meant to reference an actual partition on the target table,
            confirm the table's real partition count (its partition function's boundary value
            count, plus one) instead of assuming the value is valid.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "REBUILD PARTITION number exceeds the engine's 15000-partition ceiling",
                NoncompliantSql: """
                    ALTER TABLE dbo.Sales REBUILD PARTITION = 15001;
                    """,
                NoncompliantExplanation: "No table can ever have more than 15000 partitions, so partition number 15001 is rejected with Msg 7722 regardless of how dbo.Sales is actually partitioned.",
                CompliantSql: """
                    ALTER TABLE dbo.Sales REBUILD PARTITION = 15000;
                    """,
                CompliantExplanation: "15000 is within the engine's universal ceiling, so the statement passes this check (though it can still fail Msg 7730 if dbo.Sales' own scheme has fewer partitions)."),
        ]);
}
