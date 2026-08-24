-- Oracle-confirmed against the standing Docker instance: ALTER COLUMN from a char/nchar/varchar/
-- nvarchar column to a binary/varbinary column fails to compile with Msg 257 ("Implicit
-- conversion from data type ... to ... is not allowed. Use the CONVERT function to run this
-- query.") - and ALTER COLUMN's own syntax has no way to carry an explicit CAST/CONVERT alongside
-- the new type. The reverse direction (binary/varbinary to char/nchar/varchar/nvarchar) is not
-- flagged - oracle-confirmed that direction succeeds.
CREATE TABLE dbo.Document
(
    DocumentId INT NOT NULL PRIMARY KEY,
    Payload    VARCHAR(50) NOT NULL
);
GO
ALTER TABLE dbo.Document ALTER COLUMN Payload VARBINARY(50);
