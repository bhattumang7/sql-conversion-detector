using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class AlwaysEncryptedUnsupportedColumn
{
    public static string DataTypeRuleId => SarifRuleCatalog.AlwaysEncryptedUnsupportedColumnRuleId(AlwaysEncryptedUnsupportedColumnKind.UnsupportedDataType);

    public static string IdentityRuleId => SarifRuleCatalog.AlwaysEncryptedUnsupportedColumnRuleId(AlwaysEncryptedUnsupportedColumnKind.IdentityColumn);

    public static RuleDocContent DataTypeContent { get; } = new(
        WhyItMatters: """
            Always Encrypted stores ciphertext for an encrypted column and drives every comparison,
            sort, or aggregate over it through the client driver rather than the engine itself. That
            requires the driver to know how to serialize the plaintext value into the wire format the
            encryption algorithm expects, and a handful of data types have no such serialization path
            at all: `xml`, `json`, `timestamp`/`rowversion`, `image`, `text`, `ntext`, `sql_variant`,
            `hierarchyid`, `geography`, and `geometry` - SQL Server 2025's native `json` type is
            rejected the same way as `xml`, oracle-confirmed directly. None of these are workarounds away from being
            supported - the engine rejects the CREATE/ALTER outright the moment `ENCRYPTED WITH` is
            attached to a column of one of these types (oracle-confirmed, Msg 33280: "Cannot create or
            alter encrypted column '...' because data type '...' is not supported for encryption"),
            regardless of the encryption type (DETERMINISTIC or RANDOMIZED), the algorithm, or whether
            a secure enclave is configured. This is a pure catalog fact about the column's own declared
            type, unrelated to the connecting client or enclave family of restrictions already covered
            by the key-column rule.

            MAX-length character/binary types (`VARCHAR(MAX)`, `NVARCHAR(MAX)`, `VARBINARY(MAX)`) are
            not in this rejected set - they encrypt normally.
            """,
        HowToFixIt: """
            Store the value in a supported type instead: convert `text`/`ntext`/`image` to
            `VARCHAR(MAX)`/`NVARCHAR(MAX)`/`VARBINARY(MAX)`, serialize `xml`/`sql_variant`/`hierarchyid`/
            `geography`/`geometry` content to a supported binary or character representation before
            encrypting it, or drop `ENCRYPTED WITH` from the column if the value doesn't actually need
            Always Encrypted protection.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An xml column can never carry ENCRYPTED WITH",
                NoncompliantSql: """
                    CREATE TABLE dbo.Customer
                    (
                        CustomerId INT NOT NULL PRIMARY KEY,
                        Profile    XML
                            ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
                    );
                    """,
                NoncompliantExplanation: "xml is in the set of types Always Encrypted rejects outright - this fails to deploy with Msg 33280 regardless of encryption type or algorithm.",
                CompliantSql: """
                    CREATE TABLE dbo.Customer
                    (
                        CustomerId INT NOT NULL PRIMARY KEY,
                        Profile    NVARCHAR(MAX)
                            ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
                    );
                    """,
                CompliantExplanation: "Serializing the value into NVARCHAR(MAX) before storing it uses a type Always Encrypted actually supports."),
        ]);

    public static RuleDocContent IdentityContent { get; } = new(
        WhyItMatters: """
            An IDENTITY column's value is generated and read by the engine itself during ordinary
            INSERT processing, entirely outside the client-driver path that Always Encrypted relies on
            to encrypt and decrypt values - there is no point at which the engine could produce an
            encrypted identity value on its own. SQL Server rejects the combination outright the moment
            `ENCRYPTED WITH` is attached to an `IDENTITY` column (oracle-confirmed, Msg 2749: "Identity
            column '...' must be of data type int, bigint, smallint, tinyint, or decimal or numeric
            with a scale of 0, unencrypted, and constrained to be nonnullable"), independent of which
            of those otherwise-valid identity types is used. This is a pure catalog fact about the
            column's own declaration.
            """,
        HowToFixIt: """
            Drop `ENCRYPTED WITH` from the identity column - its value is a sequence number, not
            sensitive data - and encrypt a separate, non-identity column instead if the row also needs
            an encrypted value.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "An IDENTITY column can never carry ENCRYPTED WITH",
                NoncompliantSql: """
                    CREATE TABLE dbo.Customer
                    (
                        CustomerId INT IDENTITY(1,1)
                            ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
                    );
                    """,
                NoncompliantExplanation: "CustomerId is IDENTITY, so ENCRYPTED WITH is rejected outright - this fails to deploy with Msg 2749 regardless of the underlying integer type.",
                CompliantSql: """
                    CREATE TABLE dbo.Customer
                    (
                        CustomerId INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                        Ssn        INT
                            ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
                    );
                    """,
                CompliantExplanation: "CustomerId stays a plain identity column, and the value that actually needs protection is encrypted on a separate, non-identity column instead."),
        ]);
}
