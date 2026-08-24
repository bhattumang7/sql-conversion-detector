using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class AlterColumnIncompatibleFamilyConversion
{
    public static string RuleId => SarifRuleCatalog.AlterColumnSafetyRuleId(AlterColumnSafetyKind.IncompatibleFamilyConversion);

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            ALTER TABLE ... ALTER COLUMN can retype a column from any char/nchar/varchar/nvarchar
            type directly to binary/varbinary. Unlike a plain SELECT expression, ALTER COLUMN's
            syntax has no way to carry an explicit CAST/CONVERT alongside the new type - it can
            only name the target type. The engine has no implicit conversion from the character
            types to the binary types, so this direction fails to compile outright (oracle-
            confirmed, Msg 257, "Implicit conversion from data type ... to ... is not allowed. Use
            the CONVERT function to run this query.") - a query-level workaround that ALTER COLUMN
            itself has no syntax to apply.

            The reverse direction - binary/varbinary retyped to char/nchar/varchar/nvarchar - is
            not flagged: the engine does have an implicit conversion that way, so the same
            ALTER COLUMN statement deploys without error.
            """,
        HowToFixIt: """
            ALTER COLUMN cannot make this change directly. Add a new column of the target binary
            type, populate it with an explicit CONVERT() from the existing character column, drop
            the old column, and rename the new one into its place.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "ALTER COLUMN from VARCHAR to VARBINARY never compiles",
                NoncompliantSql: """
                    CREATE TABLE dbo.Document
                    (
                        DocumentId INT NOT NULL PRIMARY KEY,
                        Payload    VARCHAR(50) NOT NULL
                    );

                    ALTER TABLE dbo.Document ALTER COLUMN Payload VARBINARY(50);
                    """,
                NoncompliantExplanation: "There is no implicit conversion from varchar to varbinary, and ALTER COLUMN has no syntax to supply an explicit CONVERT - this fails with Msg 257.",
                CompliantSql: """
                    ALTER TABLE dbo.Document ADD PayloadBinary VARBINARY(50) NULL;
                    UPDATE dbo.Document SET PayloadBinary = CONVERT(VARBINARY(50), Payload);
                    ALTER TABLE dbo.Document DROP COLUMN Payload;
                    EXEC sp_rename 'dbo.Document.PayloadBinary', 'Payload', 'COLUMN';
                    """,
                CompliantExplanation: "An explicit CONVERT() in an UPDATE statement, followed by dropping the old column and renaming the new one, achieves the same retype outside ALTER COLUMN's own syntax."),
        ]);
}
