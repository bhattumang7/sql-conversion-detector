using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Security;

internal static class UnprovableDynamicSqlText
{
    public static string RuleId => SarifRuleCatalog.SecurityRuleId(SecurityFindingKind.UnprovableDynamicSqlText);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            An `EXEC(string)`/`EXEC(@sql)`/`sp_executesql` call site whose assembled SQL text this
            tool's own dynamic-SQL constant-folding pass could not prove is fully constant - it
            depends on a variable, parameter, or expression whose value this pass never guesses at.
            This is the SECURITY framing of exactly the call sites the tool's separate,
            performance-framed dynamic-SQL stream declines to analyze further: "this call site's
            assembled text cannot be shown, from the code alone, to be free of runtime or external
            influence" is a real, actionable SQL-injection-surface claim, distinct from the
            plan-cache-bloat concern the performance-framed sibling reports for the opposite case (a
            value that COULD be proven constant but was still concatenated into the SQL text instead
            of passed as a real parameter).

            This finding never claims the text IS actually influenced by unsanitized external
            input - this tool cannot see as far as an application boundary, so it has no way to know
            whether the variable feeding the dynamic SQL ultimately traces back to user input,
            configuration, or another trusted internal source. It only reports that the code alone
            cannot prove the text is safe, which is exactly the population a manual security review
            of dynamic SQL needs to start from. Reported at Medium confidence for that reason -
            duplicate findings at the same call site are collapsed to one, since it's the same
            underlying dynamic-SQL classification restated for each redundant occurrence, not a
            distinct risk each time.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A dynamic SQL call site whose text depends on an unprovable parameter",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_RunReport
                        @TableName SYSNAME
                    AS
                    BEGIN
                        DECLARE @sql NVARCHAR(200) = N'SELECT * FROM ' + @TableName;
                        EXEC (@sql);
                    END;
                    """,
                NoncompliantExplanation: "@TableName's value is not provably constant from the code alone - this tool cannot show the assembled SQL text is free of runtime or external influence, which is exactly the population a manual review of this dynamic SQL should start from."),
        ]);
}
