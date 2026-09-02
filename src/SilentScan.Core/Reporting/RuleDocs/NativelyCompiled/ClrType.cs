using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.NativelyCompiled;

internal static class ClrType
{
    public static string RuleId => SarifRuleCatalog.NativelyCompiledClrTypeRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A natively compiled stored procedure or scalar/inline-table-valued function
            (CREATE/ALTER ... WITH NATIVE_COMPILATION) declares a parameter or local variable
            typed as a CLR user-defined type (a type created with CREATE TYPE ... EXTERNAL
            NAME). Oracle-confirmed (Msg 10794, "The type '<name>' is not supported with
            natively compiled modules."): the CREATE/ALTER statement never compiles.

            This is decidable purely from the module's own parameter/DECLARE type references
            checked against the set of CLR UDT names seen in CREATE TYPE ... EXTERNAL NAME
            statements elsewhere in the scanned SQL - no live catalog read or knowledge of the
            CLR type's actual shape is needed, only its name.
            """,
        HowToFixIt: """
            Move the CLR UDT parameter/variable and any logic that depends on it into an
            interpreted T-SQL caller, and pass only natively-compiled-supported data
            (the UDT's serialized form, or its individual fields) into the natively compiled
            module itself.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A CLR UDT parameter on a natively compiled procedure",
                NoncompliantSql: """
                    CREATE TYPE dbo.GeoPoint EXTERNAL NAME GeoAssembly.[GeoPoint];
                    GO
                    CREATE PROCEDURE dbo.SavePoint
                        @point dbo.GeoPoint
                    WITH NATIVE_COMPILATION, SCHEMABINDING
                    AS
                    BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                        INSERT INTO dbo.Points (Point) VALUES (@point);
                    END;
                    """,
                NoncompliantExplanation: "dbo.GeoPoint is a CLR user-defined type - it is not one of the types supported inside a natively compiled module, so the CREATE PROCEDURE statement fails with error 10794 and never compiles.",
                CompliantSql: """
                    CREATE TYPE dbo.GeoPoint EXTERNAL NAME GeoAssembly.[GeoPoint];
                    GO
                    CREATE PROCEDURE dbo.SavePoint
                        @pointText NVARCHAR(100)
                    WITH NATIVE_COMPILATION, SCHEMABINDING
                    AS
                    BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                        INSERT INTO dbo.Points (PointText) VALUES (@pointText);
                    END;
                    """,
                CompliantExplanation: "Passing the CLR type's serialized text form as a plain NVARCHAR parameter avoids the unsupported type entirely; the interpreted T-SQL caller can convert to/from dbo.GeoPoint before and after calling the natively compiled procedure."),
        ]);
}
