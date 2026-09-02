using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class RestrictedImplicitAssignment
{
    public static string RuleId => SarifRuleCatalog.RestrictedImplicitAssignmentRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            sql_variant and xml are both able to hold values of many other types, which makes it easy
            to assume the engine can convert freely between them and everything else - it can't, and the
            restriction is asymmetric. Oracle-confirmed against a real engine: assigning a sql_variant-typed
            variable directly into a differently-typed target always fails to compile, regardless of what
            target type it is - Msg 206 ("Operand type clash") when the target is xml, Msg 257 ("Implicit
            conversion ... is not allowed") for every other target type tried (int, varchar, datetime, bit).
            The reverse direction (assigning any other type into a sql_variant) is always allowed - that
            half of the picture is not new. xml has its own, narrower restriction: an xml-typed target
            accepts an implicit assignment from a character or binary-family source (or another xml value),
            but from anything else - int, sql_variant, and presumably every other scalar type - it fails
            with Msg 206. Reading a value back out of xml into anything other than another xml variable
            fails too, including into character/binary types, which only convert implicitly in the other
            direction.

            None of this is a data-loss or truncation case like the other assignment findings this tool
            reports - there is no implicit conversion path at all for the restricted pairs, so the
            statement is rejected outright before it ever runs, unconditionally, regardless of the actual
            value involved. The same rejection applies to a stored procedure or function parameter typed
            sql_variant or xml being assigned to/from a local variable, since a parameter's declared type
            is exactly as fixed as a DECLARE'd local's.
            """,
        HowToFixIt: """
            Convert explicitly through a type both sides actually support. If a sql_variant instance holds
            a string, CONVERT/CAST it to (n)varchar first; if it holds numeric or other scalar data,
            convert it to that scalar type before use - there is no direct CAST from sql_variant into xml,
            so the conversion always has to go through a concrete intermediate type. Going the other way,
            extract an xml value's content (e.g. via .value()) into a concrete scalar type before assigning
            it into a sql_variant or a differently-typed target - character and binary-family targets are
            the only ones that can be built from an xml source at all, and only via explicit conversion.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Assigning a sql_variant variable directly into a differently-typed variable never compiles",
                NoncompliantSql: """
                    DECLARE @v sql_variant = 5;
                    DECLARE @i int;
                    SET @i = @v;
                    """,
                NoncompliantExplanation: "Fails to compile with Msg 257 (\"Implicit conversion from data type sql_variant to int is not allowed\") regardless of what @v actually holds.",
                CompliantSql: """
                    DECLARE @v sql_variant = 5;
                    DECLARE @i int;
                    SET @i = CAST(@v AS int);
                    """,
                CompliantExplanation: "An explicit CAST to the known underlying type compiles and runs."),
            new RuleDocExample(
                Title: "Assigning a non-string, non-binary variable into an xml variable never compiles",
                NoncompliantSql: """
                    DECLARE @i int = 5;
                    DECLARE @x xml;
                    SET @x = @i;
                    """,
                NoncompliantExplanation: "Fails to compile with Msg 206 (\"Operand type clash: int is incompatible with xml\") - xml only accepts an implicit assignment from a character/binary source or another xml value.",
                CompliantSql: """
                    DECLARE @i int = 5;
                    DECLARE @x xml;
                    SET @x = CAST(CAST(@i AS varchar(20)) AS xml);
                    """,
                CompliantExplanation: "Converting through a character type first gives the engine a real conversion path to xml."),
        ]);
}
