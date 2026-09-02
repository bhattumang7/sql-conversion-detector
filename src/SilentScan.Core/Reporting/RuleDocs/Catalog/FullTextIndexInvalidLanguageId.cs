using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Catalog;

internal static class FullTextIndexInvalidLanguageId
{
    public static string RuleId => SarifRuleCatalog.FullTextIndexDdlRuleId(FullTextIndexDdlFindingKind.InvalidLanguageId);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A full-text index column's LANGUAGE clause names a numeric locale ID (LCID) that isn't
            one of the language resources SQL Server actually ships. Oracle-confirmed (Msg 7696,
            "Invalid locale ID was specified. Please verify that the locale ID is correct and
            corresponding language resource has been installed"): the statement never deploys. The
            set of installed LCIDs (0 for language-neutral, plus every language SQL Server's
            full-text engine resource carries) is fixed per SQL Server version - it's SQL Server's
            own configuration data (sys.fulltext_languages), not something a database can add to, so
            a LANGUAGE value outside that set is decidable without a live connection. Only a plain
            numeric LCID (decimal or 0x-prefixed hex) is checked; a bare language name or a variable
            is left unchecked rather than guessed at.
            """,
        HowToFixIt: """
            Use one of the LCIDs SQL Server's full-text language resources actually cover (1033 for
            English, 1036 for French, and so on), or omit LANGUAGE entirely to fall back to the
            column's default language.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A typo'd LCID in a full-text index column",
                NoncompliantSql: """
                    CREATE TABLE dbo.Articles
                    (
                        ArticleId INT           NOT NULL PRIMARY KEY,
                        Body      NVARCHAR(MAX) NULL
                    );

                    CREATE FULLTEXT INDEX ON dbo.Articles(Body LANGUAGE 999999)
                        KEY INDEX PK__Articles;
                    """,
                NoncompliantExplanation: "999999 is not a locale ID SQL Server's full-text engine ships a language resource for, so this fails with error 7696.",
                CompliantSql: """
                    CREATE TABLE dbo.Articles
                    (
                        ArticleId INT           NOT NULL PRIMARY KEY,
                        Body      NVARCHAR(MAX) NULL
                    );

                    CREATE FULLTEXT INDEX ON dbo.Articles(Body LANGUAGE 1033)
                        KEY INDEX PK__Articles;
                    """,
                CompliantExplanation: "1033 (English) is one of the LCIDs SQL Server's full-text language resources cover."),
        ]);
}
