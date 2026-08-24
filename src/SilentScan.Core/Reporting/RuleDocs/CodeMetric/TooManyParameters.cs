using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.CodeMetric;

internal static class TooManyParameters
{
    public static string RuleId => SarifRuleCatalog.CodeMetricRuleId(CodeMetricFindingKind.TooManyParameters);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A procedure or function declares more formal parameters than the configured maximum.
            Purely a maintainability signal - no query result or execution plan is affected. A very
            long parameter list is hard for a caller to get right positionally, and often signals
            the routine is doing several distinct jobs each needing its own slice of the parameters.
            """,
        HowToFixIt: """
            Group related parameters into a table-valued parameter or a lookup they can be joined
            against, or split the routine so each piece only needs the parameters relevant to it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A procedure with a long, position-dependent parameter list",
                NoncompliantSql: "CREATE PROCEDURE dbo.UpdateCustomer (@id INT, @name NVARCHAR(100), @email NVARCHAR(200), @phone NVARCHAR(20), @addr1 NVARCHAR(200), @addr2 NVARCHAR(200), @city NVARCHAR(100), @state NVARCHAR(50), @zip NVARCHAR(20), @country NVARCHAR(100)) AS BEGIN /* ... */ END",
                NoncompliantExplanation: "Ten positional parameters make a caller easy to get wrong by passing values in the wrong order.",
                CompliantSql: "CREATE TYPE dbo.CustomerAddress AS TABLE (Line1 NVARCHAR(200), Line2 NVARCHAR(200), City NVARCHAR(100), State NVARCHAR(50), Zip NVARCHAR(20), Country NVARCHAR(100));\nCREATE PROCEDURE dbo.UpdateCustomer (@id INT, @name NVARCHAR(100), @email NVARCHAR(200), @phone NVARCHAR(20), @address dbo.CustomerAddress READONLY) AS BEGIN /* ... */ END",
                CompliantExplanation: "Grouping the address fields into a table-valued parameter shortens the parameter list and names each field explicitly."),
        ]);
}
