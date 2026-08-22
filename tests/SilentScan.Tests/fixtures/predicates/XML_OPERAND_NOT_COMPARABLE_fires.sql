-- Oracle-confirmed against the standing Docker instance: comparing two xml columns fails to
-- compile with Msg 305 ("The XML data type cannot be compared or sorted, except when using the
-- IS NULL operator") every time the statement runs.
CREATE TABLE dbo.Document
(
    DocumentId INT NOT NULL PRIMARY KEY,
    Payload    XML NOT NULL,
    Template   XML NOT NULL
);
GO
SELECT DocumentId
FROM dbo.Document
WHERE Payload = Template;
