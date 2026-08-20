using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class AlwaysEncryptedOrderBy
{
    public static string RuleId => SarifRuleCatalog.AlwaysEncryptedOrderByRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            Always Encrypted stores a protected column's real value as ciphertext, computed once at
            write time by an Always-Encrypted-aware client driver - the server itself never sees the
            plaintext, and never decrypts a stored value to satisfy a query. That design has a direct
            consequence for ORDER BY: sorting the rows by an encrypted column's stored bytes has no
            relationship to the plaintext order those bytes represent, for either supported encryption
            type. DETERMINISTIC encryption produces identical ciphertext for identical plaintext (so
            equality/GROUP BY against the same scheme can work directly on ciphertext), but produces
            no ordering guarantee at all between different plaintext values. RANDOMIZED encryption is
            even more strict: encrypting the same value twice produces different ciphertext each time,
            so ciphertext bytes carry no usable signal whatsoever.

            SQL Server does not attempt a meaningless sort - it rejects the statement outright at
            compile time (Msg 33277, "Encryption scheme mismatch for columns/variables ... expects it
            to be RANDOMIZED, a BIN2 collation for string data types, and an enclave-enabled column
            encryption key, or PLAINTEXT"), oracle-confirmed against a real, deployed Always Encrypted
            schema for both DETERMINISTIC and RANDOMIZED columns. This is unconditional - it happens
            regardless of whether the connecting client is itself Always-Encrypted-enabled, because the
            restriction is about what ordering by ciphertext could ever mean, not about who is allowed
            to decrypt it. A query that reaches this shape fails every single time it runs, in every
            environment, for every caller - the same certainty class as a genuine collation conflict.
            """,
        HowToFixIt: """
            Remove the encrypted column from the ORDER BY clause. If the application genuinely needs
            rows sorted by that column's real value, Always Encrypted has no supported mechanism for
            it server-side - sort on a different, non-encrypted column instead (a surrogate key, an
            insertion timestamp, or another column that carries the ordering information you actually
            need), or perform the sort client-side after decrypting the values through an
            Always-Encrypted-enabled connection.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "ORDER BY on an Always Encrypted column never compiles",
                NoncompliantSql: """
                    CREATE COLUMN MASTER KEY CMK1
                    WITH (KEY_STORE_PROVIDER_NAME = 'MSSQL_CERTIFICATE_STORE', KEY_PATH = 'CurrentUser/My/...');

                    CREATE COLUMN ENCRYPTION KEY CEK1
                    WITH VALUES (COLUMN_MASTER_KEY = CMK1, ALGORITHM = 'RSA_OAEP', ENCRYPTED_VALUE = 0x...);

                    CREATE TABLE dbo.Customer
                    (
                        CustomerId INT NOT NULL PRIMARY KEY,
                        Ssn        CHAR(9) COLLATE Latin1_General_BIN2
                            ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL
                    );

                    SELECT CustomerId
                    FROM dbo.Customer
                    ORDER BY Ssn;
                    """,
                NoncompliantExplanation: "Ssn is Always Encrypted (DETERMINISTIC) - this statement fails to compile with Msg 33277 every time it runs, regardless of the connection's own Always Encrypted settings.",
                CompliantSql: """
                    SELECT CustomerId
                    FROM dbo.Customer
                    ORDER BY CustomerId;
                    """,
                CompliantExplanation: "CustomerId is not encrypted, so sorting on it is unrestricted - if the real requirement was sorting by SSN specifically, that ordering has to happen client-side after decryption, not in the query itself."),
        ]);
}
