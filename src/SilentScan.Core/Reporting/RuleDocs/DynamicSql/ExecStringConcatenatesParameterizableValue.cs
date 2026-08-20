using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DynamicSql;

internal static class ExecStringConcatenatesParameterizableValue
{
    public static string RuleId => SarifRuleCatalog.ExecStringConcatenatesParameterizableValueRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            The same concatenated-value fact as the sibling `concatenated-value-in-constant-sql`
            rule, but scoped specifically to a genuine `EXEC(string)`/`EXEC(@sql)` call site - the
            sharper, more actionable claim that `sp_executesql`'s own `@params` mechanism was
            AVAILABLE and simply never used at all. A call site that already uses `sp_executesql`
            but still concatenates its value in doesn't get this finding (that call site already
            reached for the right tool, just used it wrong - the general plan-cache-pollution
            finding alone covers that case); this rule fires only when the call site's own choice of
            `EXEC(string)` over `sp_executesql` is itself what's leaving the parameterization
            mechanism on the table.

            A single `EXEC(string)` call site concatenating a value produces BOTH this finding and
            the general sibling - two distinct claims about the same underlying fact, not a
            duplicate: the general finding says "this pollutes the plan cache," this one says "and
            you had a straightforward way to avoid it that this specific call shape never used."
            """,
        HowToFixIt: """
            Use sp_executesql with its own @params mechanism instead of EXEC(string)/EXEC(@sql)
            string concatenation, so one cached plan is reused across every distinct value.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "EXEC(string) concatenating a value where sp_executesql's @params was never used",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_Find AS
                    BEGIN
                        DECLARE @Code VARCHAR(20) = 'ABC';
                        EXEC('SELECT * FROM dbo.T WHERE Code = ''' + @Code + '''');
                    END;
                    """,
                NoncompliantExplanation: "This is a plain EXEC(string) call - sp_executesql's own @params mechanism was never used at all, so every distinct @Code value compiles a fresh cached plan with no straightforward parameterized alternative already in play.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.usp_Find AS
                    BEGIN
                        DECLARE @Code VARCHAR(20) = 'ABC';
                        EXEC sp_executesql
                            N'SELECT * FROM dbo.T WHERE Code = @Code',
                            N'@Code VARCHAR(20)',
                            @Code = @Code;
                    END;
                    """,
                CompliantExplanation: "Switching to sp_executesql with its own @params mechanism means the SQL text never changes across calls, so one cached plan is reused for every distinct Code value."),
        ]);
}
