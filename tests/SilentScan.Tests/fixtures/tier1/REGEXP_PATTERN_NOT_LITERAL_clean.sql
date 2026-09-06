-- Near-miss sibling of REGEXP_PATTERN_NOT_LITERAL_fires.sql: the same predicate with a
-- literal pattern instead of a parameter. Must NOT fire.
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
