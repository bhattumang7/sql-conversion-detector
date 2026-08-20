using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.ControlFlow;

internal static class TruncateSwallowed
{
    public static string RuleId => SarifRuleCatalog.TruncateSwallowedRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A `TRUNCATE TABLE` inside a `TRY` block whose `CATCH` block never `THROW`s or
            `RAISERROR`s anywhere in its own statement tree means a real TRUNCATE failure is
            silently swallowed - execution continues past the CATCH as if the TRUNCATE had
            succeeded, and the caller never learns anything went wrong. This is a real, common
            failure mode, not a theoretical one: `TRUNCATE TABLE` fails (Msg 4712) when the target
            table is referenced by an enforced foreign key, among other real conditions - oracle-
            confirmed directly against a real engine that this exact shape produces exactly that
            silent-continuation behavior.

            T-SQL's own grammar guarantees every fixture this rule can ever see is a genuinely
            paired TRY/CATCH: a `TRY` block with no matching `CATCH` at all is a hard parse error
            (Msg 102) and can never occur in valid T-SQL, so this rule is never checking for a
            missing CATCH, only for a present-but-silent one.
            """,
        HowToFixIt: """
            Add a THROW or RAISERROR inside the CATCH block so a real TRUNCATE failure doesn't get
            silently swallowed.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A TRUNCATE whose CATCH block never re-raises",
                NoncompliantSql: """
                    BEGIN TRY
                        TRUNCATE TABLE dbo.StagingImport;
                    END TRY
                    BEGIN CATCH
                        INSERT INTO dbo.ErrorLog (Message) VALUES (ERROR_MESSAGE());
                    END CATCH;
                    """,
                NoncompliantExplanation: "If TRUNCATE fails (e.g. an enforced foreign key references dbo.StagingImport, Msg 4712), the CATCH block logs the error but never re-raises it - execution continues past this block as if the TRUNCATE had succeeded, and the caller never learns the table was never actually cleared.",
                CompliantSql: """
                    BEGIN TRY
                        TRUNCATE TABLE dbo.StagingImport;
                    END TRY
                    BEGIN CATCH
                        INSERT INTO dbo.ErrorLog (Message) VALUES (ERROR_MESSAGE());
                        THROW;
                    END CATCH;
                    """,
                CompliantExplanation: "THROW re-raises the original error after logging it, so a real TRUNCATE failure reaches the caller instead of being silently swallowed."),
        ]);
}
