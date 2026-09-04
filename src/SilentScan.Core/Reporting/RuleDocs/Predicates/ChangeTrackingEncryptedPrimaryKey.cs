using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class ChangeTrackingEncryptedPrimaryKey
{
    public static string RuleId => SarifRuleCatalog.ChangeTrackingEncryptedPrimaryKeyRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Change tracking identifies changed rows by their primary key, and the engine will not
            enable it on a table whose primary key includes an Always Encrypted column. Confirmed
            directly against a real SQL Server instance: `ALTER TABLE ... ENABLE CHANGE_TRACKING`
            fails with Msg 22118 ("Cannot enable change tracking on table '...'. Change tracking is
            not supported when the primary key contains encrypted columns.") whenever any primary
            key column is Always Encrypted - deterministic or randomized, with or without enclave
            support makes no difference. An encrypted column anywhere else on the table (not part
            of the primary key) is unaffected; change tracking enables normally.

            Both facts needed to decide this - which columns form the primary key, and which
            columns are Always Encrypted - are DDL-time catalog facts, so this is decidable without
            executing anything.
            """,
        HowToFixIt: """
            Enable change tracking on a table whose primary key columns are not Always Encrypted,
            or move the encrypted column out of the primary key before enabling change tracking.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An Always Encrypted primary key column blocks ENABLE CHANGE_TRACKING",
                NoncompliantSql: """
                    CREATE TABLE dbo.Customer
                    (
                        Ssn  NVARCHAR(20) COLLATE Latin1_General_BIN2
                             ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CustomerCek, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
                             NOT NULL PRIMARY KEY,
                        Name NVARCHAR(100) NULL
                    );

                    ALTER TABLE dbo.Customer ENABLE CHANGE_TRACKING;
                    -- Fails: Msg 22118, change tracking is not supported when the primary key
                    -- contains encrypted columns.
                    """,
                NoncompliantExplanation: "Ssn is both the primary key and Always Encrypted - ENABLE CHANGE_TRACKING fails with Msg 22118 every time it runs.",
                CompliantSql: """
                    CREATE TABLE dbo.Customer
                    (
                        Id   INT IDENTITY NOT NULL PRIMARY KEY,
                        Ssn  NVARCHAR(20) COLLATE Latin1_General_BIN2
                             ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CustomerCek, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
                             NOT NULL,
                        Name NVARCHAR(100) NULL
                    );

                    ALTER TABLE dbo.Customer ENABLE CHANGE_TRACKING;
                    """,
                CompliantExplanation: "The primary key is an ordinary INT identity column - Ssn stays Always Encrypted, but change tracking enables successfully."),
        ]);
}
