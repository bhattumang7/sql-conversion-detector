using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.NativelyCompiled;

internal static class UnsupportedBuiltin
{
    public static string RuleId => SarifRuleCatalog.NativelyCompiledUnsupportedBuiltinRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A natively compiled stored procedure or scalar/inline-table-valued function
            (CREATE/ALTER ... WITH NATIVE_COMPILATION) calls a built-in function outside the
            documented supported surface for native modules. Oracle-confirmed (Msg 10794, "The
            function '<name>' is not supported with natively compiled modules."): the
            CREATE/ALTER statement never compiles.

            The check is a denylist of specific functions, each individually oracle-confirmed
            rejected inside a natively compiled module on a current engine (UPPER, LOWER,
            REPLACE, CHARINDEX, STUFF, REVERSE, PATINDEX, QUOTENAME, DATALENGTH, ISNUMERIC,
            ISDATE, HASHBYTES, CONCAT, FORMAT, SOUNDEX, STDEV, STDEVP, VAR, VARP, STRING_SPLIT,
            LEFT, and RIGHT). LEFT/RIGHT calls are rejected the same way (Msg
            10794) even though the ScriptDom parser models them as their own node kinds rather
            than a generic function call, so they get their own scan hook rather than a
            name lookup. The supported surface for native modules is not simply the
            complement of any one documented list - some functions absent from Microsoft's own
            published list (for example DATENAME) still compile, and STRING_AGG - despite still
            appearing in Microsoft's own published unsupported-surface list - is oracle-confirmed
            to compile and run cleanly inside a natively compiled module on a current engine, so
            it is deliberately excluded from this denylist - so absence from this denylist
            is never treated as proof of support; only functions individually confirmed to fail
            are flagged, to avoid a false positive on a function that in fact compiles.
            """,
        HowToFixIt: """
            Remove the call, or move the logic that needs it into interpreted T-SQL that calls
            the natively compiled module rather than living inside it.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "UPPER() called inside a natively compiled procedure",
                NoncompliantSql: """
                    CREATE PROCEDURE dbo.NormalizeCode
                    WITH NATIVE_COMPILATION, SCHEMABINDING
                    AS
                    BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                        DECLARE @code NVARCHAR(20) = UPPER(N'ab-12');
                        INSERT INTO dbo.Codes (Code) VALUES (@code);
                    END;
                    """,
                NoncompliantExplanation: "UPPER is not one of the built-in functions supported inside a natively compiled module - the CREATE PROCEDURE statement fails with error 10794 and never compiles.",
                CompliantSql: """
                    CREATE PROCEDURE dbo.NormalizeCode
                        @code NVARCHAR(20)
                    WITH NATIVE_COMPILATION, SCHEMABINDING
                    AS
                    BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                        INSERT INTO dbo.Codes (Code) VALUES (@code);
                    END;
                    """,
                CompliantExplanation: "Uppercasing the value in interpreted T-SQL before calling the natively compiled procedure avoids the unsupported call entirely."),
        ]);
}
