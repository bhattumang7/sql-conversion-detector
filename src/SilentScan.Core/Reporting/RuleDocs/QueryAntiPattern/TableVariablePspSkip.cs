using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.QueryAntiPattern;

internal static class TableVariablePspSkip
{
    public static string RuleId => SarifRuleCatalog.QueryAntiPatternRuleId(QueryAntiPatternFindingKind.TableVariablePspSkip);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: "A table-valued parameter creates SQL Server's TableVariable Parameter Sensitive Plan skip condition at database compatibility level 170 or later. A statement that would otherwise qualify for Parameter Sensitive Plan optimization cannot receive PSP plan variants, so one cached plan must serve all parameter-value shapes.",
        HowToFixIt: "Move the PSP-sensitive statement into a procedure or function without a table-valued parameter, or use a different data-passing design when PSP plan variants are required.",
        Examples:
        [
            new RuleDocExample(
                Title: "A table-valued parameter creates the TableVariable PSP skip condition",
                NoncompliantSql: """
                    CREATE TYPE dbo.IdList AS TABLE (Id int NOT NULL PRIMARY KEY);
                    GO
                    CREATE PROCEDURE dbo.FindOrders
                        @CustomerId int,
                        @Ids dbo.IdList READONLY
                    AS
                    SELECT * FROM dbo.Orders WHERE CustomerId = @CustomerId;
                    """,
                NoncompliantExplanation: "A PSP-eligible statement in this procedure cannot receive PSP variants because the table-valued parameter establishes the engine's TableVariable skip condition.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.FindOrders
                        @CustomerId int
                    AS
                    SELECT * FROM dbo.Orders WHERE CustomerId = @CustomerId;
                    """,
                CompliantExplanation: "Without a table-valued parameter, PSP remains available when the statement otherwise qualifies."),
        ]);
}
