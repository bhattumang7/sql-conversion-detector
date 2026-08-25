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

            The same assignment happens in reverse for an OUTPUT parameter: at the end of the call,
            SQL Server implicitly converts the callee's own final parameter value to the caller-side
            variable's declared type and assigns it back. A narrower caller-side variable loses
            information at that point exactly as it would on the way in - the direction is reported
            on each finding so it's clear which side of the call is silently narrowing.
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
            new RuleDocExample(
                Title: "An OUTPUT parameter's final value silently rounded on the way back",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_ComputeTax
                        @Tax DECIMAL(10,4) OUTPUT
                    AS
                    BEGIN
                        SET @Tax = 12.3456;
                    END;

                    -- Caller:
                    DECLARE @tax DECIMAL(4,1);
                    EXEC dbo.usp_ComputeTax @Tax = @tax OUTPUT;
                    """,
                NoncompliantExplanation: "@usp_ComputeTax computes a DECIMAL(10,4) result, but @tax can only hold one fractional digit - the value is silently rounded when SQL Server copies the callee's final parameter value back into @tax at the end of the call, not when @usp_ComputeTax itself runs.",
                CompliantSql: """
                    -- Caller:
                    DECLARE @tax DECIMAL(10,4);
                    EXEC dbo.usp_ComputeTax @Tax = @tax OUTPUT;
                    """,
                CompliantExplanation: "@tax now matches the OUTPUT parameter's declared type and scale exactly - the final value crosses back over the call boundary with no implicit narrowing conversion."),
        ]);
}
