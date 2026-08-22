-- Near-miss: same table shape as XML_OPERAND_NOT_COMPARABLE_fires.sql, but the predicate compares
-- the plain (non-xml) DocumentId column instead of the xml columns - stays quiet.
CREATE TABLE dbo.Document
(
    DocumentId INT NOT NULL PRIMARY KEY,
    Payload    XML NOT NULL,
    Template   XML NOT NULL
);
GO
SELECT DocumentId
FROM dbo.Document
WHERE DocumentId = 1;
