using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Tier1;

internal static class RegexpPatternNoLiteralPrefix
{
    public static string RuleId => SarifRuleCatalog.Tier1RuleId(SargabilityFindingKind.RegexpPatternNoLiteralPrefix);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            REGEXP_LIKE only produces a seek-shaped plan when the engine can derive a bounded
            range of values the pattern can match. For a literal pattern, that derivation only
            succeeds when the whole pattern reduces to a leading anchor followed by nothing but
            literal characters - the regex equivalent of a LIKE prefix with no wildcard. Any other
            regex construct anywhere in the pattern - a missing anchor, a wildcard, a character
            class, a trailing anchor - defeats the derivation, and the engine falls back to
            scanning every row and evaluating the pattern per row.

            This is a stronger, oracle-confirmed claim than "might not seek": a literal pattern
            that isn't a pure anchored-literal string cannot seek, full stop.
            """,
        HowToFixIt: """
            Rewrite the pattern so it reduces to a leading anchor followed only by literal
            characters (e.g. '^John' instead of '[Jj]ohn' or '^Jo.*hn'), if the intent really is a
            prefix match. If the pattern genuinely needs the richer regex construct, there is no
            seek-shaped alternative for that predicate - consider a computed/persisted column that
            captures just the prefix and indexing that instead.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A literal pattern with no anchored literal prefix forces a scan",
                NoncompliantSql: """
                    CREATE TABLE dbo.Products
                    (
                        ProductId INT           NOT NULL PRIMARY KEY,
                        Sku       NVARCHAR(50)  NOT NULL
                    );
                    CREATE INDEX IX_Products_Sku ON dbo.Products(Sku);

                    SELECT ProductId
                    FROM dbo.Products
                    WHERE REGEXP_LIKE(Sku, '[Ss]ku-1');
                    """,
                NoncompliantExplanation: "The pattern has no leading anchor, so the engine cannot derive any range of values it could match and scans every row.",
                CompliantSql: """
                    SELECT ProductId
                    FROM dbo.Products
                    WHERE REGEXP_LIKE(Sku, '^Sku-1');
                    """,
                CompliantExplanation: "The pattern reduces to an anchor followed by pure literal characters, so the engine derives a seek range from it and produces an Index Seek."),
        ]);
}
