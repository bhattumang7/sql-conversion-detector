using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class OversizedParameter
{
    public static string RuleId => SarifRuleCatalog.OversizedParameterRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SQL Server sizes the memory grant it requests for sort and hash operators using the
            DECLARED size of the operands involved - the length in the parameter's, variable's, or
            expression's type declaration - not the actual length of the data that will flow through
            at runtime. A predicate comparing a column against a parameter declared far longer than
            the column itself doesn't change what data can possibly be compared (the column's own
            stored values are still bounded by its own declared length), but it can still influence
            how much memory the optimizer decides to reserve for operators downstream of that
            comparison, because the wider declared type is what the estimator sees when a value
            derived from that comparison needs to be sorted or hashed later in the plan.

            The most common real-world source is an ORM or query-building layer that defaults every
            string parameter to a generically wide type - VARCHAR(8000) or VARCHAR(MAX) is a common
            default when the ORM doesn't know (or isn't told) the target column's actual length, or
            simply always parameterizes strings the same way regardless of the schema. A column
            declared VARCHAR(20) compared against such a parameter is functionally correct - the
            comparison still returns the right rows - but the memory grant machinery downstream is
            working from the parameter's declared width, not the column's, which can request far
            more workspace memory than the query genuinely needs.

            This is a structural report about the parameter's declaration, not a claim about this
            specific predicate's plan shape - an oversized parameter doesn't prevent an index seek
            or force a scan by itself, and whether the inflated grant actually causes a measurable
            problem depends on the rest of the plan (whether a sort/hash operator downstream
            actually consumes the wide value, and how memory-constrained the instance is at that
            moment). It's flagged because the mismatch is easy to introduce silently through ORM
            defaults and easy to fix once noticed.
            """,
        HowToFixIt: """
            Declare the parameter, variable, or expression with a length that matches the column
            it's compared against, rather than a meaningfully longer one, so operators downstream
            that consume the compared value size their memory grant against the real data width
            instead of an inflated declared width. For an ORM-generated parameter, this usually
            means explicitly configuring the parameter's database type and length rather than
            accepting a generic default.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An ORM-default VARCHAR(8000) parameter against a VARCHAR(20) column",
                NoncompliantSql: """
                    CREATE TABLE dbo.Accounts
                    (
                        AccountId INT         NOT NULL PRIMARY KEY,
                        AccountNo VARCHAR(20) NOT NULL
                    );

                    CREATE PROCEDURE dbo.FindAccountsSorted (@accountNo VARCHAR(8000))
                    AS
                    SELECT AccountId, AccountNo
                    FROM dbo.Accounts
                    WHERE AccountNo = @accountNo
                    ORDER BY AccountId;
                    """,
                NoncompliantExplanation: "@accountNo is declared VARCHAR(8000) against a VARCHAR(20) column - any operator downstream that has to work with the compared value sizes its memory request off the parameter's declared 8000-character width, not the column's real 20-character width.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.FindAccountsSorted (@accountNo VARCHAR(20))
                    AS
                    SELECT AccountId, AccountNo
                    FROM dbo.Accounts
                    WHERE AccountNo = @accountNo
                    ORDER BY AccountId;
                    """,
                CompliantExplanation: "The parameter's declared length now matches the column's, so any downstream memory grant sizing reflects the data's real width instead of an ORM default far wider than the schema needs."),
        ]);
}
