-- Oracle-confirmed against the standing Docker instance: creating an index, unique/primary
-- key constraint, or statistics over a RANDOMIZED-encrypted column whose column encryption key
-- is backed by a column master key with no ENCLAVE_COMPUTATIONS clause fails to deploy with
-- Msg 33573 ("is encrypted using randomized encryption with a non enclave-enabled column
-- encryption key and is therefore not valid for use as a key column in a constraint, index, or
-- statistics"). A column master key declared WITH ENCLAVE_COMPUTATIONS does not hit this error.
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
    Ssn        INT
        ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK1, ENCRYPTION_TYPE = RANDOMIZED, ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NOT NULL
);
GO
CREATE INDEX IX_Customer_Ssn ON dbo.Customer(Ssn);
