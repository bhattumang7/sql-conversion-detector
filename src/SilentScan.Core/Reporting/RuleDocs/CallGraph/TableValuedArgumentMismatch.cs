using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.CallGraph;

internal static class TableValuedArgumentMismatch
{
    public static string RuleId => SarifRuleCatalog.ProcCallTableValuedArgumentMismatchRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A table-valued parameter argument is not the same kind of call-site conversion as a
            scalar one. SQL Server requires the caller's table variable to be declared with the
            exact same user-defined table type as the parameter itself - any mismatch there is a
            hard compile-time "Operand type clash" error, never a silent one, so there is no
            silent shape mismatch to catch at the call boundary itself.

            The real silent loss happens earlier, when the caller populates that table variable.
            An `INSERT INTO @tvp VALUES (...)` against a typed table variable is an ordinary
            assignment into the table type's own declared columns, and SQL Server applies the same
            implicit-conversion rules there as any other INSERT: a numeric value gets silently
            rounded to the column's declared scale, a Unicode string gets silently replaced with
            '?' characters outside a non-Unicode column's codepage, and a wider temporal value gets
            silently truncated to a DATE column - all without an error, all before the EXEC call
            that eventually passes the table variable even runs. String/binary length overflow is
            deliberately excluded from this rule: unlike a scalar variable assignment, SQL Server
            raises a hard "String or binary data would be truncated" error for that case on a
            table variable, so it is never silent (oracle-confirmed).
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A table variable's row silently rounds a column's declared scale before the call",
                NoncompliantSql: """
                    CREATE TYPE dbo.AmountList AS TABLE (Amount DECIMAL(10,2) NOT NULL);
                    GO
                    CREATE PROCEDURE dbo.usp_ApplyAmounts @Amounts dbo.AmountList READONLY
                    AS
                    BEGIN
                        SELECT Amount FROM @Amounts;
                    END;
                    GO
                    CREATE PROCEDURE dbo.usp_Caller
                    AS
                    BEGIN
                        DECLARE @rows dbo.AmountList;
                        INSERT INTO @rows VALUES (75.5678);
                        EXEC dbo.usp_ApplyAmounts @Amounts = @rows;
                    END;
                    """,
                NoncompliantExplanation: "75.5678 is silently rounded to 75.57 the moment it's written into @rows's DECIMAL(10,2) column - dbo.usp_ApplyAmounts never sees the original value, and no error is raised anywhere in the batch.",
                CompliantSql: """
                    DECLARE @rows dbo.AmountList;
                    INSERT INTO @rows VALUES (CAST(75.5678 AS DECIMAL(10,2)));
                    EXEC dbo.usp_ApplyAmounts @Amounts = @rows;
                    """,
                CompliantExplanation: "The value is already at the table type's own declared scale before it's written, so nothing is silently rounded away."),
        ]);
}
