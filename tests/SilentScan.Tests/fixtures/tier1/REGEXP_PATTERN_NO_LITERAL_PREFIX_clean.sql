-- Oracle-confirmed (silentscan-sql2025, SHOWPLAN_XML): a literal pattern that reduces to a
-- leading anchor followed only by literal characters produces a real Index Seek. Must NOT fire.
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
WHERE REGEXP_LIKE(DisplayName, '^John');
