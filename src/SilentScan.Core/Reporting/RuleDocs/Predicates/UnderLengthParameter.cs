using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class UnderLengthParameter
{
    public static string RuleId => SarifRuleCatalog.UnderLengthParameterRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            When a variable, parameter, or expression is declared with a length shorter than the
            value assigned to it, T-SQL doesn't raise an error or truncate at assignment loudly -
            it silently truncates the value to fit the declared length, and execution continues as
            if nothing happened. A predicate that compares a column against such a truncated value
            is not comparing against the value the author intended at all; it's comparing against
            whatever prefix survived the truncation, which changes which rows match, or causes none
            to match, with no error raised anywhere in the process. This is a correctness bug, not a
            performance one - the query returns a wrong answer that looks like a right one.

            The single most notorious trigger is DECLARE @code VARCHAR with no length specified at
            all. Outside a CAST/CONVERT context, T-SQL's default length for VARCHAR/NVARCHAR/CHAR
            with no explicit length is 1, not some larger sensible default - a mismatch between how
            the type behaves as a column default (where it's more forgiving in some contexts) and
              how it behaves as a variable declaration, and one of the most common surprises in
            T-SQL for anyone coming from a language where omitting a size means "whatever fits."
            DECLARE @code VARCHAR = 'ABCDEFG' silently stores just 'A' - not an error, not a
            warning, just a single character - and every subsequent use of @code operates on that
            one character.

            The same silent truncation happens with any variable, parameter, or expression declared
            meaningfully shorter than the column it's compared against, even with an explicit
            length: DECLARE @code VARCHAR(5) assigned 'ABCDEFG' silently becomes 'ABCDE'. A
            predicate WHERE Code = @code then searches for 'ABCDE' when the caller's actual intent
            was 'ABCDEFG' - matching a completely different row if one happens to start with
            'ABCDE', or matching nothing at all, with the query returning a plausible-looking empty
            or wrong result set instead of any indication that the comparison value was silently
            cut short before it ever reached the predicate.
            """,
        HowToFixIt: """
            Declare the parameter, variable, or expression with a length at least as long as the
            column it's compared against - ideally matching it exactly - so the value being compared
            is never silently shortened before the predicate runs. For VARCHAR/NVARCHAR/CHAR
            specifically, always give an explicit length; never rely on the bare type name's
            implicit length-1 default, which is almost never the intended behavior.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "DECLARE VARCHAR with no length silently becomes length 1",
                NoncompliantSql: """
                    CREATE TABLE dbo.Products
                    (
                        ProductId INT         NOT NULL PRIMARY KEY,
                        Code      VARCHAR(20) NOT NULL
                    );

                    DECLARE @code VARCHAR = 'ABCDEFG';

                    SELECT ProductId
                    FROM dbo.Products
                    WHERE Code = @code;
                    """,
                NoncompliantExplanation: "VARCHAR with no explicit length defaults to VARCHAR(1), so @code silently holds just 'A' instead of 'ABCDEFG' - the predicate searches for 'A', matching wrong rows or none, with no error raised at the assignment or the comparison.",
                CompliantSql: """
                    DECLARE @code VARCHAR(20) = 'ABCDEFG';

                    SELECT ProductId
                    FROM dbo.Products
                    WHERE Code = @code;
                    """,
                CompliantExplanation: "@code is declared long enough to hold the full intended value, so the predicate compares against 'ABCDEFG' in full rather than a silently truncated prefix."),
        ]);
}
