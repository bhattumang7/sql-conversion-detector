using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class LegacyLobConversionTarget
{
    public static string RuleId => SarifRuleCatalog.LegacyLobConversionTargetRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            `TEXT`/`NTEXT` cannot carry a UTF-8 or supplementary-character-aware (`_SC`) collation -
            these legacy LOB types predate both encodings. A `CAST`/`CONVERT`/`TRY_CAST`/`TRY_CONVERT`
            expression that targets `TEXT`/`NTEXT` and is followed by a `COLLATE` clause naming such a
            collation fails to compile, unconditionally.

            Oracle-confirmed (Msg 4189, "Cannot convert to text/ntext or collate to '...' because
            these legacy LOB types do not support UTF-8 or UTF-16 encodings") - the failure happens at
            compile time regardless of the source value, and the `TRY_` variants fail exactly the same
            way, since the problem is the target type/collation pairing itself, not a runtime
            conversion outcome. Decidable purely from the expression's own target type and collation
            clause - no catalog lookup needed.
            """,
        HowToFixIt: "Convert to VARCHAR(MAX)/NVARCHAR(MAX) instead of TEXT/NTEXT, or drop the UTF-8/_SC collation from the COLLATE clause.",
        Examples:
        [
            new RuleDocExample(
                Title: "CONVERT to NTEXT with a UTF-8 collation",
                NoncompliantSql: "SELECT CONVERT(ntext, Name) COLLATE Latin1_General_100_CI_AI_SC FROM dbo.Customer;",
                NoncompliantExplanation: "NTEXT cannot carry a supplementary-character-aware (_SC) collation - this statement never compiles."),
            new RuleDocExample(
                Title: "TRY_CAST to TEXT with a UTF-8 collation",
                NoncompliantSql: "SELECT TRY_CAST(Description AS text) COLLATE Latin1_General_100_CI_AS_SC_UTF8 FROM dbo.Product;",
                NoncompliantExplanation: "TRY_CAST fails the same way as CAST here - the target type/collation pairing is illegal regardless of the TRY_ variant."),
        ]);
}
