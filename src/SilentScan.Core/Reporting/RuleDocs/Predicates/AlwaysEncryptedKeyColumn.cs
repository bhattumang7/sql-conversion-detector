using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class AlwaysEncryptedKeyColumn
{
    public static string RuleId => SarifRuleCatalog.AlwaysEncryptedKeyColumnRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            RANDOMIZED encryption produces different ciphertext for the same plaintext on every
            encryption, by design - it gives the strongest confidentiality guarantee Always Encrypted
            offers, at the cost of any usable relationship between two ciphertexts of the same value.
            A B-tree index key, a PRIMARY KEY/UNIQUE constraint, and a statistics object all depend on
            being able to compare or order key values without a secure enclave doing that work; none of
            them can do that over RANDOMIZED ciphertext on their own.

            SQL Server 2019+ can bridge this gap with secure enclaves: a column master key declared
            WITH ENCLAVE_COMPUTATIONS lets the engine perform in-place comparisons inside an attested
            enclave, which makes a RANDOMIZED-encrypted column usable as a key column after all. But
            when the backing column master key was declared without that clause, the engine has no path
            to that comparison at all - the CREATE/ALTER statement is rejected outright (oracle-
            confirmed, Msg 33573: "is encrypted using randomized encryption with a non enclave-enabled
            column encryption key and is therefore not valid for use as a key column in a constraint,
            index, or statistics"). This is a pure catalog fact: the column master key's own
            declaration settles it, independent of the connecting client or any query shape.
            """,
        HowToFixIt: """
            Switch the column to DETERMINISTIC encryption if all the workload needs is equality lookups
            - deterministic ciphertext is stable for a given plaintext and works as an index/constraint
            key without an enclave. If RANDOMIZED encryption is required, declare the backing column
            master key WITH ENCLAVE_COMPUTATIONS (and have a secure-enclave-capable server available) so
            the engine has a path to the comparison. Otherwise, drop the column from the index's,
            constraint's, or statistics object's key list.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A RANDOMIZED column with a non-enclave key never deploys as an index key",
                NoncompliantSql: """
                    CREATE COLUMN MASTER KEY CMK1
                    WITH (KEY_STORE_PROVIDER_NAME = 'MSSQL_CERTIFICATE_STORE', KEY_PATH = 'CurrentUser/My/...');

                    CREATE COLUMN ENCRYPTION KEY CEK1
                    WITH VALUES (COLUMN_MASTER_KEY = CMK1, ALGORITHM = 'RSA_OAEP', ENCRYPTED_VALUE = 0x...);

                    CREATE TABLE dbo.Customer
                    (
                        CustomerId INT NOT NULL PRIMARY KEY,
                        Ssn        INT
                            ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = RANDOMIZED, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL
                    );

                    CREATE INDEX IX_Customer_Ssn ON dbo.Customer(Ssn);
                    """,
                NoncompliantExplanation: "CMK1 has no ENCLAVE_COMPUTATIONS clause, so Ssn's RANDOMIZED ciphertext has no comparable ordering the engine can build an index over - this fails to deploy with Msg 33573.",
                CompliantSql: """
                    CREATE TABLE dbo.Customer
                    (
                        CustomerId INT NOT NULL PRIMARY KEY,
                        Ssn        INT
                            ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL
                    );

                    CREATE INDEX IX_Customer_Ssn ON dbo.Customer(Ssn);
                    """,
                CompliantExplanation: "DETERMINISTIC ciphertext is stable per plaintext value, so the index key column deploys without needing a secure enclave."),
        ]);
}
