using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.NativelyCompiled;

internal static class InterpretedCallee
{
    public static string RuleId => SarifRuleCatalog.NativelyCompiledInterpretedCalleeRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A natively compiled stored procedure or scalar/inline-table-valued function
            (CREATE/ALTER ... WITH NATIVE_COMPILATION) executes another procedure or calls
            another function that is itself defined - in the scanned SQL - without
            NATIVE_COMPILATION (a plain interpreted T-SQL routine, or a CLR routine created
            with EXTERNAL NAME). Oracle-confirmed the CREATE/ALTER statement never compiles:
            EXEC against an interpreted procedure fails with Msg 12342 ("The EXECUTE statement
            in natively compiled modules only supports executing natively compiled modules."),
            and calling an interpreted scalar function fails with Msg 12344 ("Only natively
            compiled modules can be used with natively compiled modules.").

            This is decidable only against a callee whose own CREATE/ALTER PROCEDURE/FUNCTION
            is present among the scanned files; a callee whose definition isn't in the scanned
            set (a system procedure, or a routine defined elsewhere) is never treated as
            interpreted, since its native-compilation status can't be determined.
            """,
        HowToFixIt: """
            Call the interpreted routine from an interpreted T-SQL caller instead of from
            inside the natively compiled module, or - if the callee's own logic allows it -
            convert the callee itself to a natively compiled module.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A natively compiled procedure calling an interpreted procedure",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.LogAudit
                    AS
                    BEGIN
                        INSERT INTO dbo.AuditLog (Message) VALUES (N'audited');
                    END;
                    GO
                    CREATE PROCEDURE dbo.SaveOrder
                    WITH NATIVE_COMPILATION, SCHEMABINDING
                    AS
                    BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                        EXEC dbo.LogAudit;
                    END;
                    """,
                NoncompliantExplanation: "dbo.LogAudit is a plain interpreted T-SQL procedure - EXEC inside a natively compiled module can only execute another natively compiled module, so the CREATE PROCEDURE for dbo.SaveOrder fails with error 12342 and never compiles.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.LogAudit
                    AS
                    BEGIN
                        INSERT INTO dbo.AuditLog (Message) VALUES (N'audited');
                    END;
                    GO
                    CREATE PROCEDURE dbo.SaveOrder
                    WITH NATIVE_COMPILATION, SCHEMABINDING
                    AS
                    BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                        SELECT 1;
                    END;
                    GO
                    CREATE PROCEDURE dbo.SaveOrderAndLog
                    AS
                    BEGIN
                        EXEC dbo.SaveOrder;
                        EXEC dbo.LogAudit;
                    END;
                    """,
                CompliantExplanation: "Moving the call to the interpreted dbo.LogAudit procedure into an interpreted T-SQL caller that itself calls the natively compiled procedure avoids the unsupported interpreted-to-native-and-back call pattern."),
        ]);
}
