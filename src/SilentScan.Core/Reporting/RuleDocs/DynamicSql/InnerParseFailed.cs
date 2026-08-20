using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.DynamicSql;

internal static class InnerParseFailed
{
    public static string RuleId => SarifRuleCatalog.DynamicSqlInnerParseFailedRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A dynamic SQL call site's argument was successfully proven constant - this tool's own
            constant-folding pass reconstructed a definite text for it - but that reassembled text
            did not parse as T-SQL when reparsed through the same `Microsoft.SqlServer.TransactSql.
            ScriptDom` parser this tool uses everywhere else. This can happen when the assembled
            text targets a different SQL dialect entirely, or when it's genuinely malformed (a
            concatenation bug that produces syntactically broken SQL, invisible until something
            actually tries to parse the result).

            This is reported as its own distinct outcome rather than folded into `Unanalyzable`,
            because the two failure modes are meaningfully different: `Unanalyzable` means "this
            tool couldn't even determine what the text would be," while `InnerParseFailed` means
            "this tool knows exactly what the text is, and that text itself doesn't parse" - a
            stronger, more specific signal, since a definite parse failure on the ACTUAL runtime SQL
            text is worth surfacing on its own, independent of whatever else this tool can or can't
            see.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A provably-constant dynamic SQL argument that doesn't parse as T-SQL",
                NoncompliantSql: """
                    DECLARE @sql NVARCHAR(200) = N'SELEC Id FROM dbo.Products';
                    EXEC(@sql);
                    """,
                NoncompliantExplanation: "@sql's value is a single, provably-constant literal - this tool knows exactly what text will execute - but that exact text (\"SELEC\" instead of \"SELECT\") does not parse as valid T-SQL, so it's reported as InnerParseFailed rather than silently skipped or misclassified as Unanalyzable."),
        ]);
}
