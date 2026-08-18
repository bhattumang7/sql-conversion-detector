using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Tier1;

internal static class LikePatternNotLiteral
{
    public static string RuleId => SarifRuleCatalog.Tier1RuleId(SargabilityFindingKind.LikePatternNotLiteral);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            When a LIKE pattern is a literal string known at compile time - LIKE 'Smith%' - the
            optimizer can read the literal, see that it has no leading wildcard, and generate a
            seek predicate against the prefix directly. When the pattern is instead a variable or
            parameter - LIKE @pattern - the optimizer has no idea at compile time what the runtime
            value will be. It might be 'Smith%' (a seekable prefix search), or it might be
            '%Smith%' (not seekable at all), and the same compiled plan has to handle both, because
            recompiling per parameter value isn't how parameterized plans work by default. SQL
            Server's answer is to generate a plan that's correct for every possible pattern -
            which, since a leading wildcard can't be ruled out, generally means a scan, even for
            the calls where the actual runtime pattern would have been a clean prefix.

            This is a genuinely different problem from LeadingWildcardLike: it's not that the
            pattern definitely can't seek, it's that the optimizer can't PROVE it can, so it
            conservatively assumes the worst case. A search box that lets a user type any pattern -
            with or without a leading %, at their choice - runs into this every time the query is
            written as one parameterized LIKE.
            """,
        HowToFixIt: """
            If the application can determine at the call site whether the user's search term has a
            leading wildcard, branch into two differently-shaped queries - one with a literal
            trailing-wildcard-only pattern (seekable), one with the general form (not seekable) -
            instead of one query that always uses a parameter. OPTION (RECOMPILE) is the other
            common fix: it tells the engine to compile a fresh plan using the actual parameter
            value on every execution, so a call whose real pattern has no leading wildcard gets a
            seek-shaped plan for that call, at the cost of a compile on every execution instead of
            plan reuse.
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

                    CREATE PROCEDURE dbo.SearchProducts (@pattern NVARCHAR(50))
                    AS
                    SELECT ProductId
                    FROM dbo.Products
                    WHERE Sku LIKE @pattern;
                    """,
                NoncompliantExplanation: "The plan must stay correct whether @pattern arrives as 'ABC%' or '%ABC%' - the optimizer can't prove a leading wildcard is absent, so it compiles a scan-shaped plan regardless of what value is actually passed.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.SearchProducts (@pattern NVARCHAR(50))
                    AS
                    SELECT ProductId
                    FROM dbo.Products
                    WHERE Sku LIKE @pattern
                    OPTION (RECOMPILE);
                    """,
                CompliantExplanation: "With RECOMPILE, the engine compiles against @pattern's real value each call - a call passing 'ABC%' gets a seek-shaped plan for that call, at the cost of recompiling every time."),
        ]);
}
