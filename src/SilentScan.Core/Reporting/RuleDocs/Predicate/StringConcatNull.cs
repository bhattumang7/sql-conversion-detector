using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicate;

internal static class StringConcatNull
{
    public static string RuleId => SarifRuleCatalog.StringConcatNullRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Under SQL Server's default session settings (CONCAT_NULL_YIELDS_NULL ON, the ANSI
            standard and default behavior since SQL Server 2005), the `+` operator between
            string-typed operands propagates NULL: if any single operand in a `+` chain is NULL,
            the entire expression evaluates to NULL, not to the concatenation of whatever the
            non-NULL operands were. `'Mr. ' + NULL + ' Smith'` is NULL in its entirety - not
            `'Mr.  Smith'` with a gap where the middle operand would have been. This is standard,
            documented behavior, not a bug in the engine - but it's routinely not what the author
            of a display-string or full-name-building expression actually wants, and there's no
            error, warning, or type mismatch to flag the mismatch between intent and behavior; the
            expression simply evaluates to NULL and whatever consumes it - a report column, an
            email subject line, a WHERE clause - silently receives nothing.

            The setting is honored at every compatibility level, including the newest: an explicit
            `SET CONCAT_NULL_YIELDS_NULL OFF` makes `+` treat NULL as an empty string instead, for
            as long as that setting stays in effect. It is not forced on or locked - a module that
            runs with it OFF genuinely does not exhibit the NULL-propagation behavior described
            here.

            CONCAT(), added in SQL Server 2012, was specifically designed to behave differently: it
            treats a NULL argument as an empty string rather than propagating it, so
            `CONCAT('Mr. ', NULL, ' Smith')` evaluates to `'Mr.  Smith'`. The two functions are easy
            to conflate because they look interchangeable for concatenating fixed literals, and the
            difference only becomes observable the moment a genuinely NULL-capable column enters
            the expression - exactly the case that's hardest to catch in code review, since a
            reviewer has to separately know, for every operand, whether the underlying column
            allows NULL, rather than being able to see the bug in the `+` expression itself.

            The failure mode compounds in exactly the columns most likely to carry NULL in real
              data - an optional middle name, an unset suffix, a not-yet-filled-in address line 2 -
            so the rows most likely to trigger it are also the rows an author is least likely to
            have tested against, since a happy-path test with all fields populated never exercises
            the NULL-propagation branch at all.
            """,
        HowToFixIt: """
            Either wrap the nullable operand in ISNULL(column, '') or COALESCE(column, '') so a
            NULL contributes an empty string to the chain instead of nulling the whole expression,
            or replace the `+` chain with CONCAT(), which applies exactly that NULL-to-empty-string
            treatment to every argument automatically, without needing a guard on each one
            individually.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A NULL middle name nulls out the entire display name",
                NoncompliantSql: """
                    CREATE TABLE dbo.People
                    (
                        PersonId   INT          NOT NULL PRIMARY KEY,
                        FirstName  VARCHAR(50)  NOT NULL,
                        MiddleName VARCHAR(50)  NULL,
                        LastName   VARCHAR(50)  NOT NULL
                    );

                    SELECT PersonId,
                           FirstName + ' ' + MiddleName + ' ' + LastName AS DisplayName
                    FROM dbo.People;
                    """,
                NoncompliantExplanation: "For any row where MiddleName is NULL - the common case for most people - the entire DisplayName expression evaluates to NULL, not 'FirstName LastName' with the middle name simply omitted.",
                CompliantSql: """
                    SELECT PersonId,
                           CONCAT(FirstName, ' ', MiddleName, ' ', LastName) AS DisplayName
                    FROM dbo.People;
                    """,
                CompliantExplanation: "CONCAT() treats a NULL MiddleName as an empty string, so DisplayName still resolves to 'FirstName  LastName' instead of collapsing to NULL."),
        ]);
}
