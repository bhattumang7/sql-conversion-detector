using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DynamicSql;

internal static class ConcatenatedValueInConstantSql
{
    public static string RuleId => SarifRuleCatalog.ConcatenatedValueInConstantSqlRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A value this tool proved constant was spliced into an `EXEC`/`sp_executesql` dynamic
            SQL string via concatenation rather than authored as one whole literal or passed
            through `sp_executesql`'s own `@params` mechanism - on ANY call shape, whether the site
            is a plain `EXEC(string)` or a proper `sp_executesql` call. Every distinct concatenated
            value compiles its OWN separate cached plan, oracle-confirmed directly against
            `sys.dm_exec_cached_plans`: two calls differing only in the spliced literal value
            produced two distinct cached plans, where a genuinely parameterized `sp_executesql` call
            would have reused one plan across both. Under real query volume with many distinct
            values, this pollutes the plan cache with near-duplicate plans that differ only in a
            baked-in literal, wasting cache memory and compilation CPU that a single reusable
            parameterized plan would have avoided entirely.

            This is scoped specifically to a VALUE grammar position, never an identifier one - a
            concatenated table or column name is a different, often genuinely unavoidable dynamic-
            object pattern, out of scope for this finding. It also fires even when the call site
            already uses `sp_executesql` (see the sibling
            `exec-string-concatenates-parameterizable-value` rule for the sharper claim specific to
            a plain `EXEC(string)` site) - the plan-cache pollution is real regardless of which
            mechanism was used to run the assembled text, since the underlying problem is the value
            ending up baked into the SQL TEXT rather than passed as a real parameter.
            """,
        HowToFixIt: """
            Pass the value through sp_executesql's own @params mechanism instead of concatenating it
            into the SQL text, so one cached plan can be reused across every distinct value.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A literal value concatenated into an sp_executesql call's own SQL text",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_Find AS
                    BEGIN
                        DECLARE @Code VARCHAR(20) = 'ABC';
                        DECLARE @sql NVARCHAR(MAX) = N'SELECT * FROM dbo.T WHERE Code = ''' + @Code + '''';
                        EXEC sp_executesql @sql;
                    END;
                    """,
                NoncompliantExplanation: "The proc already reached for sp_executesql, but @Code was still concatenated directly into the SQL text instead of passed as a real parameter - a different @Code value compiles a whole new cached plan every time, exactly as if sp_executesql had never been used at all.",
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
                CompliantExplanation: "@Code is now passed through sp_executesql's own @params mechanism - the SQL text itself never changes across calls, so one cached plan is reused for every distinct Code value."),
        ]);
}
