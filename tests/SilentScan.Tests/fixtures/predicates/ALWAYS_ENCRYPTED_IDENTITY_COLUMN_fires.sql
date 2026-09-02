-- Oracle-confirmed against the standing Docker instance: attaching ENCRYPTED WITH to an
-- IDENTITY column fails to deploy with Msg 2749 ("must be ... unencrypted"), independent of
-- which otherwise-valid identity type is used.
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
    CustomerId INT
        ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = DETERMINISTIC, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256')
        IDENTITY
);
