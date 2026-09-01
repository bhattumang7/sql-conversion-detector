using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class SparseColumnDisallowedType
{
    public static string RuleId => SarifRuleCatalog.SparseColumnDisallowedTypeRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A SPARSE column can never be TEXT, NTEXT, IMAGE, GEOMETRY, GEOGRAPHY, a user-defined
            type, or TIMESTAMP/ROWVERSION - oracle-confirmed (Msg 1731, "A sparse column cannot
            be of the following data types: text, ntext, image, geometry, geography, or
            user-defined type"; TIMESTAMP/ROWVERSION confirmed separately) the CREATE or ALTER
            never compiles, decidable purely from the column's own declared type and SPARSE flag.
            XML, HIERARCHYID, and SQL_VARIANT are all oracle-confirmed to remain allowed as
            sparse - don't assume every "unusual" type is on the disallow-list.
            """,
        HowToFixIt: """
            Drop the SPARSE property from this column, or change its type to one the engine
            allows as sparse.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Sparse column of a disallowed type",
                NoncompliantSql: """
                    CREATE TABLE dbo.Document (Id INT NOT NULL PRIMARY KEY, Body NTEXT SPARSE NULL);
                    """,
                NoncompliantExplanation: "NTEXT is on the engine's own sparse-column disallow-list, so the CREATE TABLE fails with Msg 1731.",
                CompliantSql: """
                    CREATE TABLE dbo.Document (Id INT NOT NULL PRIMARY KEY, Body NVARCHAR(MAX) SPARSE NULL);
                    """,
                CompliantExplanation: "NVARCHAR(MAX) is not on the disallow-list, so the sparse column compiles."),
        ]);
}
