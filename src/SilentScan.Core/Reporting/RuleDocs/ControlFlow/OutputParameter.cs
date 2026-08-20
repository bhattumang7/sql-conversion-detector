using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ControlFlow;

internal static class OutputParameter
{
    public static string RuleId => SarifRuleCatalog.OutputParameterRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A procedure parameter declared `OUTPUT` reaches a `RETURN`, or the natural end of the
            module body, on some statically reachable path with no intervening assignment (`SET
            @p = ...`, `SELECT @p = ...`, or passing `@p` onward as an OUTPUT argument to another
            call) at the same scope - a real, path-sensitive reachability walk, not a heuristic, so
            a parameter assigned on SOME paths but left unassigned on others still fires, since the
            defect is per-path, not per-procedure.

            Oracle-confirmed directly against a real engine: on the unassigned path, the caller's
            own variable is left COMPLETELY UNCHANGED by the call - not reset to NULL, not zeroed,
            simply whatever it already held before the call. That makes this a genuinely dangerous,
            easy-to-miss defect: a caller that reuses the same variable across several calls (a
            common pattern in a loop, or a batch of related calls sharing local variables) can
            silently carry forward stale data from a previous, unrelated call whenever the current
            call happens to take the unassigned path - with nothing about the call site itself
            looking wrong.
            """,
        HowToFixIt: """
            Assign the OUTPUT parameter on every statically reachable path, including RETURN and the
            natural end of the body.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An OUTPUT parameter left unassigned on the early-RETURN path",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.usp_GetOrderTotal
                        @OrderId INT,
                        @Total DECIMAL(10,2) OUTPUT
                    AS
                    BEGIN
                        IF NOT EXISTS (SELECT 1 FROM dbo.Orders WHERE Id = @OrderId)
                        BEGIN
                            RETURN;
                        END

                        SELECT @Total = SUM(Amount) FROM dbo.OrderLines WHERE OrderId = @OrderId;
                    END;
                    """,
                NoncompliantExplanation: "The early RETURN path never assigns @Total at all - the caller's own variable is left completely unchanged, so if it was reused from a previous call, it silently carries that stale value forward as if it were this order's real total.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.usp_GetOrderTotal
                        @OrderId INT,
                        @Total DECIMAL(10,2) OUTPUT
                    AS
                    BEGIN
                        IF NOT EXISTS (SELECT 1 FROM dbo.Orders WHERE Id = @OrderId)
                        BEGIN
                            SET @Total = NULL;
                            RETURN;
                        END

                        SELECT @Total = SUM(Amount) FROM dbo.OrderLines WHERE OrderId = @OrderId;
                    END;
                    """,
                CompliantExplanation: "@Total is explicitly assigned on every path, including the early RETURN, so the caller's variable always reflects this call's own real outcome rather than possibly stale data from an earlier call."),
        ]);
}
