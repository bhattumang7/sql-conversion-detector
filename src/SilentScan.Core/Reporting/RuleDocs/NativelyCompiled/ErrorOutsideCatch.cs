using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.NativelyCompiled;

internal static class ErrorOutsideCatch
{
    public static string RuleId => SarifRuleCatalog.NativelyCompiledErrorOutsideCatchRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A natively compiled stored procedure or scalar/inline-table-valued function
            (CREATE/ALTER ... WITH NATIVE_COMPILATION) calls ERROR_MESSAGE(), ERROR_NUMBER(),
            ERROR_SEVERITY(), ERROR_STATE(), ERROR_LINE(), or ERROR_PROCEDURE() outside a CATCH
            block. Oracle-confirmed (Msg 10792, "...cannot appear outside of a catch block."):
            the CREATE/ALTER statement never compiles.

            This is decidable purely from the module's own CATCH-block nesting - no catalog
            read is needed, only tracking whether the call site is inside the CATCH statement
            list of a TRY/CATCH inside the same natively compiled module.
            """,
        HowToFixIt: """
            Move the ERROR_* call into the CATCH block of a TRY/CATCH inside the same natively
            compiled module.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An ERROR_NUMBER() call outside a CATCH block",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.LogLastError
                    WITH NATIVE_COMPILATION, SCHEMABINDING
                    AS
                    BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                        DECLARE @lastError INT = ERROR_NUMBER();
                    END;
                    """,
                NoncompliantExplanation: "ERROR_NUMBER() is only meaningful (and only supported inside a natively compiled module) within a CATCH block; called here it fails with error 10792 and the CREATE PROCEDURE never compiles.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.LogLastError
                    WITH NATIVE_COMPILATION, SCHEMABINDING
                    AS
                    BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                        DECLARE @lastError INT;
                        BEGIN TRY
                            SELECT 1 / 0;
                        END TRY
                        BEGIN CATCH
                            SET @lastError = ERROR_NUMBER();
                        END CATCH
                    END;
                    """,
                CompliantExplanation: "Moving the ERROR_NUMBER() call inside the CATCH block puts it in the only context a natively compiled module supports it in, so the statement compiles."),
        ]);
}
