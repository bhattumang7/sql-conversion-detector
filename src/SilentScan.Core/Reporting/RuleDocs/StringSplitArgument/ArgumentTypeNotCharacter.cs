using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.StringSplitArgument;

internal static class ArgumentTypeNotCharacter
{
    public static string RuleId => SarifRuleCatalog.StringSplitArgumentRuleId(StringSplitArgumentFindingKind.ArgumentTypeNotCharacter);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            STRING_SPLIT's first two parameters (the string to split and the separator) only accept
            character-family types. This was probed directly against a real engine: an int, numeric,
            money, datetime, uniqueidentifier, varbinary, or sql_variant argument in either position -
            whether a bare literal or a declared local variable/parameter of that type - raises Msg
            8116 ("Argument data type ... is invalid for argument N of string_split function") at
            compile/bind time, before a single row is read. This is a declared-type check, not a
            runtime-value check - it fires the same way regardless of what the argument's actual value
            would be.

            The check only fires when the argument's type is statically known - a bare literal, or a
            variable/parameter whose DECLARE or procedure signature is visible in the same module. A
            column reference or expression whose type cannot be resolved from source and catalog data
            is left alone rather than guessed at.
            """,
        HowToFixIt: """
            Change the argument to a char/varchar/nchar/nvarchar-typed expression, or CAST/CONVERT it
            to one explicitly.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "STRING_SPLIT with a non-character first argument",
                NoncompliantSql: "DECLARE @Id INT = 12345; SELECT value FROM STRING_SPLIT(@Id, ',');",
                NoncompliantExplanation: "@Id is declared INT - STRING_SPLIT's first argument only accepts character types, so the call raises Msg 8116 before any row is read.",
                CompliantSql: "DECLARE @Id INT = 12345; SELECT value FROM STRING_SPLIT(CAST(@Id AS VARCHAR(20)), ',');",
                CompliantExplanation: "Casting the value to a character type first makes it a valid STRING_SPLIT argument."),
        ]);
}
