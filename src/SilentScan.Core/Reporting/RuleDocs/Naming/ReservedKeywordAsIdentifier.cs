using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Naming;

internal static class ReservedKeywordAsIdentifier
{
    public static string RuleId => SarifRuleCatalog.NamingReservedKeywordAsIdentifierRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            T-SQL's grammar refuses to parse a true reserved keyword as an unquoted identifier at
            all, so the only way a table, column, procedure, or index ever ends up named `[order]`
            or `[transaction]` is by deliberately bracket- or quote-delimiting it at creation time.
            Once that name exists, every future reference to it - in every script, every ad hoc
            query, every other object's definition - has to remember to delimit it again, or the
            statement fails to parse. This is a permanent, self-inflicted tax on every future
            reference: a plain `SELECT order FROM ...` will not compile, and the fix is invisible
            until someone hits it.

            This is a purely syntactic, catalog-free check: any identifier - table, column,
            procedure, function, view, trigger, or index name - spelled identically (case-
            insensitively) to an entry in the official reserved keyword list is flagged the moment
            it's declared, regardless of whether it happens to be delimited correctly at the
            declaration site itself.
            """,
        HowToFixIt: """
            Rename the identifier to something that isn't a reserved keyword. If renaming isn't an
            option (an existing production object), accept that every reference to it must always be
            bracket- or quote-delimited, and treat that as a standing maintenance cost.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A table named after a reserved keyword",
                NoncompliantSql: "CREATE TABLE dbo.[order] (Id INT NOT NULL);",
                NoncompliantExplanation: "ORDER is a reserved T-SQL keyword - every future reference to this table, in every script, must bracket-delimit the name or fail to parse.",
                CompliantSql: "CREATE TABLE dbo.Orders (Id INT NOT NULL);",
                CompliantExplanation: "Orders is not a reserved keyword, so it never needs delimiting anywhere it's referenced."),
            new RuleDocExample(
                Title: "A column named after a reserved keyword",
                NoncompliantSql: "CREATE TABLE dbo.T (Id INT NOT NULL, [select] INT NULL);",
                NoncompliantExplanation: "SELECT is a reserved keyword - any query touching this column (SELECT [select] FROM dbo.T) has to delimit the column name to parse at all.",
                CompliantSql: "CREATE TABLE dbo.T (Id INT NOT NULL, SelectedFlag INT NULL);",
                CompliantExplanation: "SelectedFlag carries the same meaning without colliding with a keyword."),
        ]);
}
