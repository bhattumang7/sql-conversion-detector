-- Oracle-confirmed (silentscan-sql2025, SHOWPLAN_XML): a literal pattern lacking a leading
-- anchor forces an Index Scan even against an indexed column, so it must be flagged.
CREATE TABLE dbo.Users
(
    UserId      INT           NOT NULL PRIMARY KEY,
    DisplayName NVARCHAR(40)  NOT NULL
);
GO
CREATE INDEX IX_Users_DisplayName ON dbo.Users(DisplayName);
GO

SELECT UserId
FROM dbo.Users
WHERE REGEXP_LIKE(DisplayName, '[Jj]ohn');
