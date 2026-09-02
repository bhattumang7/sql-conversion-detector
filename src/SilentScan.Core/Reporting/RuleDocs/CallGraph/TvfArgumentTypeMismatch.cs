using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.CallGraph;

internal static class TvfArgumentTypeMismatch
{
    public static string RuleId => SarifRuleCatalog.TvfCallArgumentMismatchRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            This rule follows a real call site, not just an inline table-valued function's own
            declaration: it resolves a `FROM dbo.SomeFunction(...)` or `CROSS/OUTER APPLY
            dbo.SomeFunction(...)` reference, looks up the literal or caller-side variable actually
            passed at each argument position, and compares its type against the function's declared
            parameter type. Table-valued function arguments are always positional in T-SQL - there is
            no named-parameter syntax the way there is for `EXEC` - so the same silent narrowing that
            can affect a stored procedure's own parameter marshalling can affect an inline TVF's
            parameter marshalling too: a shorter string literal or variable, a narrower numeric type,
            or a coarser temporal type is implicitly converted to the function's declared parameter
            type before the function body's own `RETURN (SELECT ...)` ever runs.

            This is classified the same way an INSERT or UPDATE assignment's silent data loss is,
            because the underlying mechanism is identical - it's an assignment, not a predicate. The
            fact that the assignment happens at an inline TVF's own parameter boundary rather than
            inside a DML statement doesn't change the mechanism, only where in the code the loss
            happens - which is also why it's easy to miss in review, since neither the call site's own
            argument expression nor the function's own parameter declaration looks wrong in isolation,
            only the pairing across the call site does. Inline TVFs have no OUTPUT parameters, so this
            rule only has one direction to check, unlike the equivalent EXEC call-site rule.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A wider caller-side variable silently truncated at the call site",
                NoncompliantSql: """
                    CREATE FUNCTION dbo.fn_OrdersByCode (@Code VARCHAR(3))
                    RETURNS TABLE
                    AS
                    RETURN (SELECT OrderId, Code FROM dbo.Orders WHERE Code = @Code);

                    -- Caller:
                    DECLARE @code VARCHAR(10) = 'ABCDEF';
                    SELECT * FROM dbo.fn_OrdersByCode(@code);
                    """,
                NoncompliantExplanation: "@code is VARCHAR(10), but the function's own parameter is VARCHAR(3) - the value is silently truncated to 'ABC' when SQL Server implicitly converts it at parameter marshalling, before the function body's own RETURN clause ever runs, and the mismatch is invisible unless the two declarations are compared side by side.",
                CompliantSql: """
                    -- Caller:
                    DECLARE @code VARCHAR(3) = 'ABC';
                    SELECT * FROM dbo.fn_OrdersByCode(@code);
                    """,
                CompliantExplanation: "The caller's variable now matches the function's declared parameter width exactly - the value crosses the call boundary with no implicit narrowing conversion."),
        ]);
}
