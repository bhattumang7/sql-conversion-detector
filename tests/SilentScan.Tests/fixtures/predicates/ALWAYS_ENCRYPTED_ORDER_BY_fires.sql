-- Source: Microsoft Learn, "Always Encrypted (Database Engine)" - documented restrictions:
-- an encrypted column cannot be referenced in an ORDER BY clause, for either encryption type.
-- https://learn.microsoft.com/en-us/sql/relational-databases/security/encryption/always-encrypted-database-engine
-- Oracle-confirmed against the standing Docker instance: this statement fails to compile with
-- Msg 33277 ("Encryption scheme mismatch") for both DETERMINISTIC and RANDOMIZED columns alike,
-- regardless of whether the connecting client is itself Always-Encrypted-enabled.
CREATE COLUMN MASTER KEY CMK1
WITH (
    KEY_STORE_PROVIDER_NAME = 'MSSQL_CERTIFICATE_STORE',
    KEY_PATH = 'CurrentUser/My/0000000000000000000000000000000000000000'
);
GO
CREATE COLUMN ENCRYPTION KEY CEK1
WITH VALUES
(
    COLUMN_MASTER_KEY = CMK1,
    ALGORITHM = 'RSA_OAEP',
    ENCRYPTED_VALUE = 0x01000000
);
GO
CREATE TABLE dbo.Customer
(
    CustomerId INT NOT NULL PRIMARY KEY,
    Ssn        CHAR(9) COLLATE Latin1_General_BIN2
        ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL
);
GO
SELECT CustomerId
FROM dbo.Customer
ORDER BY Ssn;
