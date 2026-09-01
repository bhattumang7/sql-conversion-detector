using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class LegacyLobUtf8Collation
{
    public static string RuleId => SarifRuleCatalog.LegacyLobUtf8CollationRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            TEXT/NTEXT columns can never carry a collation with the _UTF8 or _SC
            (supplementary-character-aware) flag - oracle-confirmed (Msg 4188, "The legacy LOB
            types do not support UTF-8 or UTF-16 encodings. Use types varchar(max), nvarchar(max)
            or a collation which does not have the _SC or _UTF8 flags") the CREATE/ALTER never
            compiles, whether the flagged collation comes from an explicit COLLATE clause on the
            column or from the database's own default collation.
            """,
        HowToFixIt: """
            Migrate the column to VARCHAR(MAX)/NVARCHAR(MAX), or use a collation without the _SC
            or _UTF8 flag.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "NTEXT column with a supplementary-character-aware collation",
                NoncompliantSql: """
                    CREATE TABLE dbo.Article (ArticleId INT NOT NULL PRIMARY KEY, Body NTEXT COLLATE Latin1_General_100_CI_AS_SC NULL);
                    """,
                NoncompliantExplanation: "Latin1_General_100_CI_AS_SC carries the _SC flag, so the CREATE TABLE fails with Msg 4188.",
                CompliantSql: """
                    CREATE TABLE dbo.Article (ArticleId INT NOT NULL PRIMARY KEY, Body NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC NULL);
                    """,
                CompliantExplanation: "NVARCHAR(MAX) supports the _SC flag, so the column compiles."),
        ]);
}
