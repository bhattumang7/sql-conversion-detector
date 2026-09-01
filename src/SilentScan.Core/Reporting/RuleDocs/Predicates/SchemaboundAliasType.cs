using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class SchemaboundAliasType
{
    public static string RuleId => SarifRuleCatalog.SchemaboundAliasTypeRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A WITH SCHEMABINDING function can never declare a parameter, scalar RETURNS type, or
            multi-statement table-valued RETURNS @table column using an alias type created with
            CREATE TYPE ... FROM - oracle-confirmed (Msg 2792, "Cannot specify a sql CLR type in
            a Schema-bound object or a constraint expression") the statement fails to compile
            regardless of the alias's own underlying type, decidable purely from the function's
            own declared parameter/return types and the database's own CREATE TYPE aliases.
            """,
        HowToFixIt: """
            Replace the alias type with its underlying system type in the parameter, RETURNS
            clause, or table column, or drop WITH SCHEMABINDING if binding to the alias type is
            required.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Schemabound function with an alias-typed parameter",
                NoncompliantSql: """
                    CREATE TYPE dbo.PositiveInt FROM INT;
                    GO
                    CREATE FUNCTION dbo.Double(@x dbo.PositiveInt) RETURNS INT WITH SCHEMABINDING
                    AS BEGIN RETURN @x * 2 END;
                    """,
                NoncompliantExplanation: "@x is declared as the alias type dbo.PositiveInt, so the schemabound CREATE FUNCTION fails with Msg 2792.",
                CompliantSql: """
                    CREATE FUNCTION dbo.Double(@x INT) RETURNS INT WITH SCHEMABINDING
                    AS BEGIN RETURN @x * 2 END;
                    """,
                CompliantExplanation: "INT is a system type, not an alias, so the schemabound function compiles."),
        ]);
}
