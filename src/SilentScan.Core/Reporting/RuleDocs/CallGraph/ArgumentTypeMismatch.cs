using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.CallGraph;

internal static class ArgumentTypeMismatch
{
    public static string RuleId => SarifRuleCatalog.ProcCallArgumentMismatchRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            This rule follows the actual call graph, not just a single procedure's own declaration:
            it resolves a real `EXEC dbo.SomeProc @arg = @callerVariable` call site, looks up the
            caller's own declared type for `@callerVariable`, and compares it against the callee's
            declared parameter type from `sys.parameters`. When the caller's type risks losing
            information on the way in - a DECIMAL variable with fewer fractional digits than the
            parameter expects, an INT passed where the parameter is a narrower type, a string
            variable shorter than the parameter's declared length - the value is silently narrowed
            or truncated during parameter marshalling, before the procedure body ever runs a single
            statement. No error is raised for an implicit narrowing conversion at parameter binding;
            the procedure simply receives a value that's already lost precision, scale, or characters
            relative to what the caller thought it was passing.

            This is classified the same way an INSERT or UPDATE assignment's silent data loss is,
            because the underlying mechanism is identical - it's an assignment, not a predicate. A
            parameter binding at a call site is SQL Server implicitly converting a source value to a
            target's declared type and assigning it, exactly like an INSERT column list assigning
            expressions to column types; the fact that the assignment happens at a procedure boundary
            rather than inside a DML statement doesn't change the mechanism, only where in the code
            the loss happens - which is also why it's easy to miss in review, since neither the
            caller's variable declaration nor the callee's parameter declaration looks wrong in
            isolation, only the pairing across the call site does.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A narrower caller variable silently truncated at the call site",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_ApplyDiscount
                        @DiscountRate DECIMAL(9,4)
                    AS
                    BEGIN
                        UPDATE dbo.Products SET Price = Price * (1 - @DiscountRate);
                    END;

                    -- Caller:
                    DECLARE @rate INT = 1;
                    EXEC dbo.usp_ApplyDiscount @DiscountRate = @rate;
                    """,
                NoncompliantExplanation: "@rate is INT, so the value passed for a DECIMAL(9,4) parameter can only ever be a whole number - any fractional discount rate the caller intended (0.15 for 15%, say) is impossible to express through this variable, and the mismatch is invisible unless the two declarations are compared side by side.",
                CompliantSql: """
                    -- Caller:
                    DECLARE @rate DECIMAL(9,4) = 0.15;
                    EXEC dbo.usp_ApplyDiscount @DiscountRate = @rate;
                    """,
                CompliantExplanation: "The caller's variable now matches the parameter's declared type and scale exactly - the value crosses the call boundary with no implicit narrowing conversion."),
        ]);
}
