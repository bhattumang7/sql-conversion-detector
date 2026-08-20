-- Near-miss: same table shape as ALWAYS_ENCRYPTED_ORDER_BY_fires.sql, but ORDER BY sorts on the
-- plain (non-encrypted) CustomerId column instead of the Always Encrypted Ssn column - stays
-- quiet, matching the scanner's own per-column encryption_type check.
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
ORDER BY CustomerId;
