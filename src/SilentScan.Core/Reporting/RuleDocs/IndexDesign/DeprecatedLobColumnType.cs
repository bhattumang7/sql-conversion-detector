using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.IndexDesign;

internal static class DeprecatedLobColumnType
{
    public static string RuleId => SarifRuleCatalog.IndexDesignRuleId(IndexDesignFindingKind.DeprecatedLobColumnType);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A column declared `text`, `ntext`, or `image` has been formally deprecated by Microsoft
            since SQL Server 2005 in favor of `varchar(max)`/`nvarchar(max)`/`varbinary(max)`, and
            Microsoft's own documentation states outright that a future version may remove them
            entirely. This is a genuine functional deprecation, not merely a naming recommendation:
            these three types cannot be used in most string functions, cannot appear in a
            WHERE/GROUP BY/ORDER BY without extra casting gymnastics, and cannot be a
            variable/parameter type in many contexts the MAX-length equivalents support natively.

            This is a catalog-only, structural fact about the column's declared type - it fires
            independent of whether any scanned query actually touches the column, the same shape
            this codebase's own `max-typed-column` rule already established for a related concern.
            """,
        HowToFixIt: """
            Use the MAX-length equivalent (varchar(max)/nvarchar(max)/varbinary(max)) instead of
            text/ntext/image.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A column declared with the deprecated TEXT type",
                NoncompliantSql: """
                    CREATE TABLE dbo.Articles
                    (
                        Id INT NOT NULL PRIMARY KEY,
                        Body TEXT NULL
                    );
                    """,
                NoncompliantExplanation: "TEXT is functionally deprecated - it cannot be used in most string functions without extra casting, and Microsoft's own documentation states a future version may remove it entirely.",
                CompliantSql: """
                    CREATE TABLE dbo.Articles
                    (
                        Id INT NOT NULL PRIMARY KEY,
                        Body VARCHAR(MAX) NULL
                    );
                    """,
                CompliantExplanation: "VARCHAR(MAX) supports the same effectively-unbounded content but works natively with string functions, WHERE/GROUP BY/ORDER BY, and as a variable/parameter type."),
        ]);
}
