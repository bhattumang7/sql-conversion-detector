using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class CheckConstraintNullNotHandled
{
    public static string RuleId => SarifRuleCatalog.CheckConstraintNullNotHandledRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            SQL Server predicate evaluation is three-valued, not two-valued: any comparison
            involving NULL evaluates to UNKNOWN, not FALSE. A CHECK constraint only rejects a row
            when its predicate evaluates to FALSE for that row - UNKNOWN is treated the same as
            TRUE for the purpose of accepting or rejecting the row, exactly like a WHERE clause
            treats UNKNOWN rows as "don't include" rather than "actively excluded", except here the
            polarity works in the row's favor: an UNKNOWN CHECK result lets the row through.

            That means a CHECK constraint on a nullable column that never explicitly tests that
            column for NULL doesn't do what its own text suggests. CHECK (Age > 0) on a nullable
            Age column reads as "Age must be positive" - but a row with Age = NULL evaluates
            NULL > 0 to UNKNOWN, not FALSE, so the constraint doesn't reject it. The constraint
            silently permits exactly the value a reader would assume it forbids, and there's no
            error, no warning, nothing in the DDL that flags the gap - only working through the
            three-valued-logic truth table for a NULL input reveals it.

            This matters most for constraints that read as gatekeeping rules ("must be positive",
            "must be one of these codes", "must be after this date") - the author's intent is
            almost always to forbid bad data, and NULL sailing through untested is very rarely the
            intended behavior, which is exactly what makes this a routine, unnoticed data-quality
            hole rather than a deliberate design choice.
            """,
        HowToFixIt: """
            Add an explicit IS NULL or IS NOT NULL test for the column in the CHECK constraint's own
            predicate, so a NULL value isn't silently accepted through three-valued logic. If NULL
            is meant to be an allowed state for the column (e.g. "positive, or not yet known"),
            write that intent directly: CHECK (Age > 0 OR Age IS NULL). If NULL should genuinely be
            forbidden alongside non-positive values, make that explicit too: CHECK (Age IS NOT NULL
            AND Age > 0). Either version makes the constraint's actual behavior match its own
            wording, instead of leaving the NULL case to fall out of three-valued logic by accident.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "CHECK (Age > 0) silently admits NULL",
                NoncompliantSql: """
                    CREATE TABLE dbo.Users
                    (
                        UserId INT NOT NULL PRIMARY KEY,
                        Age    INT NULL,
                        CONSTRAINT CK_Users_Age CHECK (Age > 0)
                    );

                    INSERT INTO dbo.Users (UserId, Age) VALUES (1, NULL);
                    """,
                NoncompliantExplanation: "NULL > 0 evaluates to UNKNOWN, not FALSE, so the CHECK doesn't reject the row - the INSERT succeeds even though the constraint reads as forbidding non-positive ages.",
                CompliantSql: """
                    CREATE TABLE dbo.Users
                    (
                        UserId INT NOT NULL PRIMARY KEY,
                        Age    INT NULL,
                        CONSTRAINT CK_Users_Age CHECK (Age IS NOT NULL AND Age > 0)
                    );
                    """,
                CompliantExplanation: "NULL is now tested explicitly, so an INSERT with Age = NULL evaluates the AND to FALSE and is rejected - matching what the constraint's wording already implied."),
            new RuleDocExample(
                Title: "Making NULL an intentionally allowed state instead",
                NoncompliantSql: """
                    CREATE TABLE dbo.Users
                    (
                        UserId INT NOT NULL PRIMARY KEY,
                        Age    INT NULL,
                        CONSTRAINT CK_Users_Age CHECK (Age > 0)
                    );
                    """,
                NoncompliantExplanation: "The constraint's own text gives no indication that NULL is meant to be allowed - the fact that it currently is comes only from three-valued logic, not from an explicit choice.",
                CompliantSql: """
                    CREATE TABLE dbo.Users
                    (
                        UserId INT NOT NULL PRIMARY KEY,
                        Age    INT NULL,
                        CONSTRAINT CK_Users_Age CHECK (Age > 0 OR Age IS NULL)
                    );
                    """,
                CompliantExplanation: "Behaves identically to the original at runtime, but now the predicate itself documents that NULL ('age not yet known') is a deliberately permitted state, not an accident of NULL comparison semantics."),
        ]);
}
