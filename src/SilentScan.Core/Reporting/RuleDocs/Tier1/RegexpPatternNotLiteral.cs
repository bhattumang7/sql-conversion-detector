using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Tier1;

internal static class RegexpPatternNotLiteral
{
    public static string RuleId => SarifRuleCatalog.Tier1RuleId(SargabilityFindingKind.RegexpPatternNotLiteral);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            REGEXP_LIKE only ever produces a seek-shaped plan in the narrow case where the engine
            can prove, at compile time, exactly what range of values the pattern can match - the
            same kind of literal-prefix analysis LIKE gets, but for the much richer pattern
            language a regular expression allows. That analysis needs the actual pattern text in
            hand while compiling the plan. When the pattern is a variable or parameter instead of a
            literal string, the engine has no idea at compile time what the runtime pattern will
            be, so it can't prove anything about the range of values it might match and falls back
            to scanning every row, regardless of what the pattern would have turned out to allow at
            runtime.

            This is the direct REGEXP_LIKE analogue of LikePatternNotLiteral: it isn't that the
            predicate definitely can't seek, it's that the optimizer can't prove it can, so it
            conservatively assumes the worst case for every call using that plan.
            """,
        HowToFixIt: """
            If the application can determine the pattern shape at the call site, branch into
            differently-shaped queries instead of parameterizing the pattern into one REGEXP_LIKE
            call. OPTION (RECOMPILE) is the other common fix: it tells the engine to compile a
            fresh plan using the actual pattern value on every execution, so a call whose real
            pattern would have allowed a seek gets a seek-shaped plan for that call, at the cost of
            a compile on every execution instead of plan reuse.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A parameterized pattern can't be proven seekable",
                NoncompliantSql: """
                    CREATE TABLE dbo.Products
                    (
                        ProductId INT           NOT NULL PRIMARY KEY,
                        Sku       NVARCHAR(50)  NOT NULL
                    );
                    CREATE INDEX IX_Products_Sku ON dbo.Products(Sku);

                    CREATE PROCEDURE dbo.SearchProductsByPattern (@pattern NVARCHAR(50))
                    AS
                    SELECT ProductId
                    FROM dbo.Products
                    WHERE REGEXP_LIKE(Sku, @pattern);
                    """,
                NoncompliantExplanation: "The plan must stay correct no matter what @pattern turns out to be at runtime - the optimizer can't prove anything about the pattern shape ahead of time, so it compiles a scan-shaped plan regardless of what value is actually passed.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.SearchProductsByPattern (@pattern NVARCHAR(50))
                    AS
                    SELECT ProductId
                    FROM dbo.Products
                    WHERE REGEXP_LIKE(Sku, @pattern)
                    OPTION (RECOMPILE);
                    """,
                CompliantExplanation: "With RECOMPILE, the engine compiles against @pattern's real value each call, so a call whose real pattern would allow a seek gets a seek-shaped plan for that call, at the cost of recompiling every time."),
        ]);
}
