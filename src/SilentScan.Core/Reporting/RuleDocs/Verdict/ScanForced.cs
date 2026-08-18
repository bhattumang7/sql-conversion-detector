using SilentScan.Core.Reporting.Sarif;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Reporting.RuleDocs.Verdict;

internal static class ScanForced
{
    public static string RuleId => SarifRuleCatalog.VerdictRuleId(Rules.Verdict.ScanForced);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            This is the automatic sibling of CastOrConvertOnColumn: instead of the author writing
            a CAST/CONVERT directly in the source text, SQL Server inserts one itself, invisibly,
            whenever a comparison's two sides have different data types. SQL Server's data type
            precedence rules decide which side has to convert to match the other - and critically,
            those rules are about the TYPES involved, not about which side is the indexed column.
            When a VARCHAR column is compared against an NVARCHAR literal, or an INT column against
            a string parameter, the precedence rules can require the COLUMN side to convert, not
            the value side - and the instant that happens, the predicate is comparing a converted
            expression, not the column's raw stored value, and no index on the column can be
            seeked. Nothing in the source text looks wrong; the column is compared directly, no
            function call is visible anywhere. The conversion is entirely implicit, which is what
            makes this class of problem so easy to ship without noticing - the query returns
            correct results, just slowly, and only an execution plan (or a static analysis pass
            like this one) reveals the hidden CONVERT_IMPLICIT sitting on the column side.

            The single most common real-world trigger is an ORM or application layer sending an
            NVARCHAR parameter (the .NET/Java/etc. default string type) against a VARCHAR column -
            a mismatch that's invisible in the C#/Java source and only visible by comparing the
            column's DDL against the parameter's declared type.
            """,
        HowToFixIt: """
            Make both sides of the comparison the same type, and make sure the CONVERSION - if one
            is genuinely still needed - lands on the value side, not the column side. For a
            parameter, declare it with the column's exact type (including length and, for
            string types, collation) instead of a default/inferred type. For an ORM, this usually
            means explicitly specifying the parameter's database type rather than letting the ORM
            infer it from the .NET/Java type, since that inference is exactly what produces an
            NVARCHAR parameter against a VARCHAR column. For a literal, prefix a string literal
            being compared to a VARCHAR column with no N (plain 'text', not N'text') so it doesn't
            default to NVARCHAR.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An NVARCHAR parameter against a VARCHAR column",
                NoncompliantSql: """
                    CREATE TABLE dbo.Accounts
                    (
                        AccountId  INT          NOT NULL PRIMARY KEY,
                        AccountNo  VARCHAR(20)  NOT NULL
                    );
                    CREATE INDEX IX_Accounts_AccountNo ON dbo.Accounts(AccountNo);

                    CREATE PROCEDURE dbo.FindAccount (@accountNo NVARCHAR(20))
                    AS
                    SELECT AccountId
                    FROM dbo.Accounts
                    WHERE AccountNo = @accountNo;
                    """,
                NoncompliantExplanation: "NVARCHAR outranks VARCHAR in SQL Server's type precedence, so the engine implicitly converts AccountNo (the column) to NVARCHAR before comparing - IX_Accounts_AccountNo can't be seeked through that conversion.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.FindAccount (@accountNo VARCHAR(20))
                    AS
                    SELECT AccountId
                    FROM dbo.Accounts
                    WHERE AccountNo = @accountNo;
                    """,
                CompliantExplanation: "The parameter now matches the column's own type exactly - no conversion is needed on either side, and the index seeks normally."),
        ]);
}
