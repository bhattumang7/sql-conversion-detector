using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class TimestampColumnNaming
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.TimestampColumnNaming);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A column declared `timestamp` is, since SQL Server 2005, functionally identical to a
            `rowversion`-declared column - `rowversion` is a literal synonym for the exact same
            underlying 8-byte auto-incrementing binary type. Confirmed directly against the engine:
            `sys.columns`/`sys.types` report a `rowversion`-declared column identically to a
            `timestamp`-declared one (both resolve to system type id 80, name "timestamp" - there is
            no separate "rowversion" row in `sys.types` to tell them apart at the catalog level at
            all).

            This is deliberately NOT the same claim as the sibling `deprecated-lob-column-type`
            rule: `timestamp` is not a distinct, functionally deprecated type the way `text`/`ntext`/
            `image` are. Microsoft's own documentation recommends `rowversion` for new development
            purely because the name no longer collides with the unrelated SQL-standard `TIMESTAMP`
            datetime type and reads correctly - a naming-only recommendation, not a functional
            deprecation, and reported at Low confidence as purely informational for exactly that
            reason.
            """,
        HowToFixIt: """
            Use rowversion instead of timestamp - they're the identical type, but rowversion is the
            non-deprecated name.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A column declared with the confusingly-named TIMESTAMP type",
                NoncompliantSql: """
                    CREATE TABLE dbo.Audited
                    (
                        Id INT NOT NULL PRIMARY KEY,
                        RowVer TIMESTAMP NOT NULL
                    );
                    """,
                NoncompliantExplanation: "TIMESTAMP works identically to ROWVERSION (they're the same underlying type), but the name is easy to confuse with the unrelated SQL-standard TIMESTAMP datetime type - a naming-only concern, not a functional one.",
                CompliantSql: """
                    CREATE TABLE dbo.Audited
                    (
                        Id INT NOT NULL PRIMARY KEY,
                        RowVer ROWVERSION NOT NULL
                    );
                    """,
                CompliantExplanation: "ROWVERSION is the identical type under a name that doesn't collide with the SQL-standard TIMESTAMP concept."),
        ]);
}
