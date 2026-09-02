using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Graph;

internal static class PseudoColumnAssignment
{
    public static string RuleId => SarifRuleCatalog.GraphPseudoColumnAssignmentRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `$node_id`/`$edge_id` are pseudo-columns backed by hidden, system-managed columns on a
            SQL Graph node/edge table - the engine assigns and maintains their value itself, and
            rejects any attempt to supply or change it directly.

            Oracle-confirmed on all statement shapes: an `INSERT` naming `$node_id`/`$edge_id` in
            its column list fails ("Cannot insert the value NULL into column 'graph_id_...'"), and an
            `UPDATE` targeting the pseudo-column fails ("cannot be modified because it is either a
            computed column..."). A `MERGE` statement's own `WHEN NOT MATCHED THEN INSERT` and
            `WHEN MATCHED THEN UPDATE` actions fail the same way. This is decidable purely from the
            statement's own column references - no catalog lookup is needed, since the restriction
            holds for every graph node/edge table's `$node_id`/`$edge_id`, unconditionally.
            """,
        HowToFixIt: "Remove $node_id/$edge_id from the INSERT column list or UPDATE SET clause (including inside a MERGE statement's own actions) - the engine assigns and maintains these values itself.",
        Examples:
        [
            new RuleDocExample(
                Title: "INSERT naming $node_id explicitly",
                NoncompliantSql: "INSERT INTO dbo.Person ($node_id, Name) VALUES (DEFAULT, 'Alice');",
                NoncompliantExplanation: "$node_id is a hidden, system-managed column - supplying a value for it in the INSERT column list always fails."),
            new RuleDocExample(
                Title: "UPDATE targeting $edge_id",
                NoncompliantSql: "UPDATE dbo.Follows SET $edge_id = $edge_id WHERE Id = 1;",
                NoncompliantExplanation: "$edge_id cannot be modified because it is a computed, system-managed column - this UPDATE always fails."),
            new RuleDocExample(
                Title: "MERGE's own INSERT action naming $node_id explicitly",
                NoncompliantSql: """
                    MERGE dbo.Person AS tgt
                    USING dbo.PersonStaging AS src ON tgt.Id = src.Id
                    WHEN NOT MATCHED THEN INSERT ($node_id, Name) VALUES (DEFAULT, src.Name);
                    """,
                NoncompliantExplanation: "The same hidden, system-managed column restriction applies inside MERGE's own WHEN NOT MATCHED THEN INSERT action - this always fails."),
        ]);
}
